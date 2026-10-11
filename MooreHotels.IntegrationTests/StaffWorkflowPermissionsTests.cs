using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class StaffWorkflowPermissionsTests(ManualTransferTestFixture fixture)
{
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object body, TestUser actor)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return await fixture.Client.SendAsync(request);
    }

    [Theory]
    [InlineData(false, "Admin", null)]
    [InlineData(false, "Manager", "")]
    [InlineData(true, "Admin", "")]
    [InlineData(true, "Manager", null)]
    public async Task Verified_transfer_without_bank_id_is_audited_and_only_credited_once(bool review, string role, string? reference)
    {
        var booking = await fixture.CreateBookingAsync(paymentStatus: review ? PaymentStatus.PaymentReported : PaymentStatus.AwaitingVerification);
        var actor = role == "Admin" ? fixture.Admin : fixture.Manager;
        var path = $"/api/bookings/{booking.BookingCode}/{(review ? "review-transfer" : "confirm-transfer")}";
        var body = new
        {
            decision = "Confirm",
            confirmationText = review ? "VERIFY" : "ACCEPT",
            bankReference = reference,
            amount = booking.Amount,
            reason = "Payment verified in the hotel bank statement."
        };
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(HttpMethod.Post, path, body, actor)));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        var stored = await fixture.WithDbAsync(db => db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id));
        Assert.Equal(PaymentStatus.Paid, stored.PaymentStatus);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(actor.Id, stored.PaymentConfirmedByUserId);
        Assert.StartsWith("MANUAL-", stored.TransactionReference);
        var payments = await fixture.WithDbAsync(db => db.FolioEntries.AsNoTracking()
            .Where(e => e.Folio!.BookingId == booking.Id && e.Type == FolioEntryType.Payment).ToListAsync());
        var payment = Assert.Single(payments);
        Assert.Null(payment.ExternalReference);
        Assert.Equal(booking.Amount, payment.Amount);
        Assert.Equal(actor.Id, payment.PostedByUserId);
        Assert.True(await fixture.WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityId == booking.Id.ToString() &&
            a.Action == (review ? "TRANSFER_REVIEW_CONFIRM" : "MANUAL_PAYMENT_CONFIRMED"))));
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData(false, "abc")]
    [InlineData(true, "abc")]
    [InlineData(false, "MANUAL-internal-reference")]
    [InlineData(true, "MANUAL-internal-reference")]
    public async Task Supplied_bank_id_still_requires_valid_bank_evidence(bool review, string reference)
    {
        var booking = await fixture.CreateBookingAsync(paymentStatus: review ? PaymentStatus.PaymentReported : PaymentStatus.AwaitingVerification);
        using var response = await Send(HttpMethod.Post,
            $"/api/bookings/{booking.BookingCode}/{(review ? "review-transfer" : "confirm-transfer")}",
            new
            {
                decision = "Confirm",
                confirmationText = review ? "VERIFY" : "ACCEPT",
                bankReference = reference,
                amount = booking.Amount,
                reason = "Payment verified in the hotel bank statement."
            }, fixture.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await fixture.WithDbAsync(db => db.FolioEntries.AnyAsync(e => e.Folio!.BookingId == booking.Id && e.Type == FolioEntryType.Payment)));
    }

    [Fact]
    public async Task Refund_reconciliation_still_requires_bank_evidence()
    {
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        using var response = await Send(HttpMethod.Post, $"/api/bookings/{booking.BookingCode}/review-transfer",
            new
            {
                decision = "Refund",
                confirmationText = "VERIFY",
                amount = booking.Amount,
                reason = "Payment verified in the hotel bank statement."
            }, fixture.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Manager")]
    public async Task Cleaner_starts_but_management_alone_confirms_cleaning_and_releases_room(string role)
    {
        var manager = role == "Admin" ? fixture.Admin : fixture.Manager;
        var cleaner = await fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var room = await fixture.CreateRoomAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var cleaning = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(room.Id, null,
            HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, null), manager.Id);
        using var started = await Send(HttpMethod.Put, $"/api/housekeeping/tasks/{cleaning.Id}", new { status = "inProgress" }, cleaner);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var denied = await Send(HttpMethod.Put, $"/api/housekeeping/tasks/{cleaning.Id}", new { status = "completed" }, cleaner);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("administrator or manager", await denied.Content.ReadAsStringAsync());
        Assert.Equal(RoomStatus.Cleaning, await ReadRoomStatus());
        Assert.Equal(OperationalTaskStatus.InProgress, await fixture.WithDbAsync(db => db.HousekeepingTasks.Where(t => t.Id == cleaning.Id).Select(t => t.Status).SingleAsync()));
        using var completed = await Send(HttpMethod.Put, $"/api/housekeeping/tasks/{cleaning.Id}", new { status = "completed" }, manager);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var inspection = (await service.GetTasksAsync()).Single(t => t.RoomId == room.Id && t.Type == HousekeepingTaskType.Inspection);
        using var createDenied = await Send(HttpMethod.Post, "/api/housekeeping/tasks",
            new { roomId = room.Id, type = "inspection", priority = "normal" }, cleaner);
        Assert.Equal(HttpStatusCode.Forbidden, createDenied.StatusCode);
        using var inspectionDenied = await Send(HttpMethod.Put, $"/api/housekeeping/tasks/{inspection.Id}", new { status = "inProgress" }, cleaner);
        Assert.Equal(HttpStatusCode.Forbidden, inspectionDenied.StatusCode);
        using var inspectionStarted = await Send(HttpMethod.Put, $"/api/housekeeping/tasks/{inspection.Id}", new { status = "inProgress" }, manager);
        Assert.Equal(HttpStatusCode.OK, inspectionStarted.StatusCode);
        foreach (var passed in new[] { false, true })
        {
            using var releaseDenied = await Send(HttpMethod.Put, $"/api/housekeeping/tasks/{inspection.Id}", new { status = "completed", inspectionPassed = passed }, cleaner);
            Assert.Equal(HttpStatusCode.Forbidden, releaseDenied.StatusCode);
            Assert.Equal(RoomStatus.Inspected, await ReadRoomStatus());
        }
        using var released = await Send(HttpMethod.Put, $"/api/housekeeping/tasks/{inspection.Id}", new { status = "completed", inspectionPassed = true }, manager);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(MooreHotels.Domain.Enums.RoomStatus.Available, await ReadRoomStatus());
        Task<MooreHotels.Domain.Enums.RoomStatus> ReadRoomStatus() => fixture.WithDbAsync(db => db.Rooms.Where(r => r.Id == room.Id).Select(r => r.Status).SingleAsync());
    }
}
