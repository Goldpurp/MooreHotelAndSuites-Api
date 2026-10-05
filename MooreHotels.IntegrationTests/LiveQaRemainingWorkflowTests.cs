using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Services;

namespace MooreHotels.IntegrationTests;

// Additional audit coverage only. All records/providers belong to the isolated fixture.
[Collection(ManualTransferCollection.Name)]
public sealed class LiveQaRemainingWorkflowTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task Replacement_records_both_room_ids_and_emails_the_new_room_once()
    {
        var booking = await fixture.CreateBookingAsync(createdAtUtc: DateTime.UtcNow.AddHours(-2));
        var spare = await fixture.CreateRoomAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBookingRepository>()
            .CancelExpiredUnconfirmedAsync(DateTime.UtcNow);
        await fixture.WithDbAsync(async db =>
        {
            (await db.Bookings.SingleAsync(b => b.Id == booking.Id)).PaymentStatus = PaymentStatus.PaymentReported;
            (await db.Rooms.SingleAsync(r => r.Id == spare.Id)).RoomTypeId = booking.RoomTypeId;
            return await db.SaveChangesAsync();
        });
        var service = scope.ServiceProvider.GetRequiredService<PaymentReviewService>();
        var request = new ResolveTransferRequest("Confirm", "VERIFY", "Isolated QA bank credit simulation.",
            $"QA-BANK-{Guid.NewGuid():N}", booking.Amount, [spare.Id]);
        var result = await service.ResolveAsync(booking.BookingCode, request, fixture.Manager.Id, default);
        Assert.Equal("Confirmed", result.Status);
        Assert.Equal("Paid", result.PaymentStatus);
        var audit = await fixture.WithDbAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.Action == "PAYMENT_REVIEW_ROOM_REASSIGNED" && a.EntityId == booking.Id.ToString()));
        using var details = JsonDocument.Parse(audit.NewDataJson!);
        Assert.Equal(booking.RoomId, details.RootElement.GetProperty("PreviousRoomId").GetGuid());
        Assert.Equal(spare.Id, details.RootElement.GetProperty("ReplacementRoomId").GetGuid());
        Assert.Equal(fixture.Manager.Id, audit.ProfileId);
        var guestEmail = await fixture.WithDbAsync(db => db.Guests.Where(g => g.Id == booking.GuestId)
            .Select(g => g.Email).SingleAsync());
        await fixture.FlushEmailOutboxAsync();
        var email = Assert.Single(fixture.Email.Messages, m =>
            m.Template == "PaymentSuccess" && m.BookingCode == booking.BookingCode);
        Assert.Equal(spare.Name, email.RoomName);
        Assert.Equal(guestEmail, email.Recipient);
        await fixture.FlushEmailOutboxAsync();
        Assert.Single(fixture.Email.Messages, m =>
            m.Template == "PaymentSuccess" && m.BookingCode == booking.BookingCode);
    }

    [Fact]
    public async Task Failed_inspection_creates_one_reclean_then_one_new_inspection_and_releases()
    {
        var cleaner = await fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.CheckedIn);
        await using var scope = fixture.Services.CreateAsyncScope();
        var housekeeping = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        await scope.ServiceProvider.GetRequiredService<IBookingService>()
            .UpdateStatusAsync(booking.Id, BookingStatus.CheckedOut, fixture.Manager.Id);
        var cleaning = Assert.Single(await housekeeping.GetTasksAsync(), t =>
            t.BookingId == booking.Id && t.Type == HousekeepingTaskType.CheckoutCleaning);
        await CompleteTask(housekeeping, cleaning.Id, cleaner.Id);
        var inspection = Assert.Single(await housekeeping.GetTasksAsync(), t =>
            t.BookingId == booking.Id && t.Type == HousekeepingTaskType.Inspection);
        await CompleteTask(housekeeping, inspection.Id, fixture.Manager.Id, false);
        Assert.Equal(RoomStatus.Dirty, await Status(booking.RoomId!.Value));
        // Replaying the failed inspection must not enqueue duplicate corrective work.
        await housekeeping.UpdateTaskAsync(inspection.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Completed, fixture.Manager.Id, false, "QA inspection failed"),
            fixture.Manager.Id);
        var reclean = Assert.Single(await housekeeping.GetTasksAsync(), t =>
            t.BookingId == booking.Id && t.Type == HousekeepingTaskType.CheckoutCleaning &&
            t.Status != OperationalTaskStatus.Completed);
        await CompleteTask(housekeeping, reclean.Id, cleaner.Id);
        var secondInspection = Assert.Single(await housekeeping.GetTasksAsync(), t =>
            t.BookingId == booking.Id && t.Type == HousekeepingTaskType.Inspection &&
            t.Status != OperationalTaskStatus.Completed);
        await CompleteTask(housekeeping, secondInspection.Id, fixture.Manager.Id, true);
        Assert.Equal(RoomStatus.Available, await Status(booking.RoomId.Value));
        var tasks = (await housekeeping.GetTasksAsync()).Where(t => t.BookingId == booking.Id).ToArray();
        Assert.Equal(4, tasks.Length);
        Assert.All(tasks, task => Assert.Equal(OperationalTaskStatus.Completed, task.Status));
    }

    [Fact]
    public async Task Maintenance_assigned_progress_resolve_requires_cleaning_before_release()
    {
        var engineer = await fixture.CreateUserAsync(UserRole.Staff, "Engineering");
        var room = await fixture.CreateRoomAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var housekeeping = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var today = scope.ServiceProvider.GetRequiredService<IHotelTimeService>().Today;
        var order = await housekeeping.CreateWorkOrderAsync(new CreateMaintenanceWorkOrderRequest(
            room.Id, "QA repair", "Isolated repair workflow", WorkPriority.High,
            today, today.AddDays(2), engineer.Id), fixture.Manager.Id);
        Assert.Equal(RoomStatus.OutOfOrder, await Status(room.Id));
        await housekeeping.UpdateWorkOrderAsync(order.Id, new UpdateMaintenanceWorkOrderRequest(
            MaintenanceWorkOrderStatus.InProgress, engineer.Id, "QA work started"), engineer.Id);
        Assert.Equal(RoomStatus.OutOfOrder, await Status(room.Id));
        await housekeeping.UpdateWorkOrderAsync(order.Id, new UpdateMaintenanceWorkOrderRequest(
            MaintenanceWorkOrderStatus.Resolved, engineer.Id, "QA repair completed"), engineer.Id);
        Assert.Equal(RoomStatus.Dirty, await Status(room.Id));
        var recovery = Assert.Single(await housekeeping.GetTasksAsync(), t =>
            t.RoomId == room.Id && t.Type == HousekeepingTaskType.MaintenanceRecovery);
        Assert.Equal(OperationalTaskStatus.Pending, recovery.Status);
    }

    [Theory]
    [InlineData("invoice", false)]
    [InlineData("invoice", true)]
    [InlineData("report", false)]
    [InlineData("report", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    public async Task Another_guests_token_or_account_cannot_read_or_mutate_booking(string action, bool useAccount)
    {
        var ownerBooking = await fixture.CreateBookingAsync();
        var target = await fixture.CreateBookingAsync();
        var actor = await fixture.CreateUserAsync(UserRole.Client);
        var token = BookingGuestAccess.GenerateToken();
        await fixture.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == actor.Id)).GuestId = ownerBooking.GuestId;
            var owned = await db.Bookings.SingleAsync(b => b.Id == ownerBooking.Id);
            owned.GuestAccessTokenHash = BookingGuestAccess.Hash(token);
            owned.GuestAccessTokenIssuedAtUtc = DateTime.UtcNow;
            owned.GuestAccessTokenExpiresAtUtc = DateTime.UtcNow.AddDays(1);
            return await db.SaveChangesAsync();
        });
        var path = action switch
        {
            "invoice" => $"/api/bookings/{target.BookingCode}/invoice.pdf",
            "report" => $"/api/bookings/{target.BookingCode}/report-transfer",
            _ => "/api/bookings/guest/cancel"
        };
        using var request = new HttpRequestMessage(action == "invoice" ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-Moore-App-Environment", "local");
        if (useAccount) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        else if (action == "invoice") request.Headers.Add("X-Booking-Access-Token", token);
        if (action == "report") request.Content = JsonContent.Create(new ReportTransferRequest(useAccount ? null : token));
        if (action == "cancel") request.Content = JsonContent.Create(new CancelBookingRequest(
            target.BookingCode, useAccount ? null : token, "QA cross-owner rejection"));
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(action == "invoice" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, response.StatusCode);
        var stored = await fixture.WithDbAsync(db => db.Bookings.AsNoTracking().SingleAsync(b => b.Id == target.Id));
        Assert.Equal(target.Status, stored.Status);
        Assert.Equal(target.PaymentStatus, stored.PaymentStatus);
    }

    [Fact]
    public async Task Password_reset_rejects_wrong_token_accepts_once_then_rejects_replay()
    {
        var actor = await fixture.CreateUserAsync(UserRole.Client);
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(actor.Id.ToString()))!;
        var raw = await users.GeneratePasswordResetTokenAsync(user);
        var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
        const string localPassword = "LocalQaResetOnly123!";
        object Payload(string token) => new
        {
            UserId = actor.Id,
            Token = token,
            NewPassword = localPassword,
            ConfirmNewPassword = localPassword
        };
        using var invalid = await fixture.Client.PostAsJsonAsync("/api/auth/reset-password", Payload("invalid-token-for-qa-only"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var valid = await fixture.Client.PostAsJsonAsync("/api/auth/reset-password", Payload(encoded));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var replay = await fixture.Client.PostAsJsonAsync("/api/auth/reset-password", Payload(encoded));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    private Task<RoomStatus> Status(Guid id) => fixture.WithDbAsync(db => db.Rooms
        .Where(r => r.Id == id).Select(r => r.Status).SingleAsync());

    private static async Task CompleteTask(IHousekeepingService service, Guid id, Guid actor, bool? passed = null)
    {
        await service.UpdateTaskAsync(id, new UpdateHousekeepingTaskRequest(
            OperationalTaskStatus.InProgress, actor, null, null), actor);
        await service.UpdateTaskAsync(id, new UpdateHousekeepingTaskRequest(
            OperationalTaskStatus.Completed, actor, passed, "Isolated QA"), actor);
    }
}
