using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Services;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class PaymentReviewTests(ManualTransferTestFixture fixture)
{
    private async Task<T> Service<T>(Func<PaymentReviewService, Task<T>> operation)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<PaymentReviewService>());
    }

    private async Task Expire()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBookingRepository>().CancelExpiredUnconfirmedAsync(DateTime.UtcNow);
    }

    private async Task<string> GiveToken(Booking b)
    {
        var token = BookingGuestAccess.GenerateToken();
        await fixture.WithDbAsync(async db =>
        {
            var stored = await db.Bookings.SingleAsync(x => x.Id == b.Id);
            stored.GuestAccessTokenHash = BookingGuestAccess.Hash(token);
            stored.GuestAccessTokenIssuedAtUtc = DateTime.UtcNow;
            stored.GuestAccessTokenExpiresAtUtc = DateTime.UtcNow.AddDays(7);
            return await db.SaveChangesAsync();
        });
        return token;
    }

    [Fact]
    public async Task Http_report_queue_and_manager_review_complete_the_full_contract()
    {
        var b = await fixture.CreateBookingAsync();
        var token = await GiveToken(b);
        using var report = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{b.BookingCode}/report-transfer")
        { Content = JsonContent.Create(new ReportTransferRequest(token)) };
        report.Headers.Add("X-Moore-App-Environment", "local");
        using var reportResponse = await fixture.Client.SendAsync(report);
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        await fixture.WithDbAsync(async db =>
        {
            (await db.AuditLogs.SingleAsync(a => a.Action == "PAYMENT_REPORTED" && a.EntityId == b.Id.ToString())).CreatedAt = DateTime.UtcNow.AddHours(-2);
            return await db.SaveChangesAsync();
        });
        using var queue = new HttpRequestMessage(HttpMethod.Get, "/api/bookings/payment-reviews");
        queue.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Manager.Token);
        queue.Headers.Add("X-Moore-App-Environment", "local");
        using var queueResponse = await fixture.Client.SendAsync(queue);
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);
        var items = await queueResponse.Content.ReadFromJsonAsync<PaymentReviewItem[]>();
        Assert.Contains(items!, item => item.BookingCode == b.BookingCode && item.Overdue && item.RoomHeld);
        using var review = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{b.BookingCode}/review-transfer")
        { Content = JsonContent.Create(Request(b)) };
        review.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Manager.Token);
        review.Headers.Add("X-Moore-App-Environment", "local");
        using var reviewResponse = await fixture.Client.SendAsync(review);
        Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
        Assert.Equal(PaymentStatus.Paid, await fixture.WithDbAsync(db => db.Bookings.Where(x => x.Id == b.Id).Select(x => x.PaymentStatus).SingleAsync()));
    }

    private ResolveTransferRequest Request(Booking b, string decision = "Confirm", string? reference = null) =>
        new(decision, "VERIFY", "Checked the hotel's bank statement and contacted the guest.", reference ?? $"BANK-{Guid.NewGuid():N}", b.Amount);

    [Fact]
    public async Task Reconciled_credit_can_be_completed_through_existing_refund_controls()
    {
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        await Service(s => s.ResolveAsync(b.BookingCode, Request(b, "Refund"), fixture.Admin.Id, default));
        await using var scope = fixture.Services.CreateAsyncScope();
        var bookingService = scope.ServiceProvider.GetRequiredService<IBookingService>();
        var result = await bookingService.CompleteRefundAsync(b.Id,
            new CompleteRefundRequest($"REFUND-{Guid.NewGuid():N}", b.Amount, "BankTransfer", "BankStatement", "Synthetic local refund evidence"), fixture.Admin.Id);
        Assert.Equal(PaymentStatus.Refunded, result.PaymentStatus);
        var balance = await fixture.WithDbAsync(async db => FolioAccounting.Calculate(await db.FolioEntries.Where(e => e.Folio!.BookingId == b.Id).ToListAsync()));
        Assert.Equal(0, balance.GuestCredit);
        Assert.Equal(b.Amount, balance.Refunds);
    }

    [Fact]
    public async Task Missing_reservation_inventory_cannot_be_silently_restored()
    {
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        await fixture.WithDbAsync(async db =>
        {
            db.ReservationRooms.RemoveRange(await db.ReservationRooms.Where(r => r.BookingId == b.Id).ToListAsync());
            return await db.SaveChangesAsync();
        });
        await Assert.ThrowsAsync<BadRequestException>(() => Service(s => s.ResolveAsync(b.BookingCode, Request(b), fixture.Admin.Id, default)));
    }

    [Fact]
    public async Task Report_keeps_an_old_hold_in_inventory_and_never_marks_it_paid()
    {
        var b = await fixture.CreateBookingAsync();
        var token = await GiveToken(b);
        await Service(s => s.ReportAsync(b.BookingCode, token, null, default));
        await fixture.WithDbAsync(async db =>
        {
            (await db.Bookings.SingleAsync(x => x.Id == b.Id)).CreatedAt = DateTime.UtcNow.AddHours(-2);
            return await db.SaveChangesAsync();
        });
        await Expire();
        var stored = await fixture.WithDbAsync(db => db.Bookings.AsNoTracking().SingleAsync(x => x.Id == b.Id));
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Equal(PaymentStatus.PaymentReported, stored.PaymentStatus);
        Assert.Null(stored.PaymentConfirmedAtUtc);
        await using var scope = fixture.Services.CreateAsyncScope();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var availability = await scope.ServiceProvider.GetRequiredService<IInventoryService>().GetAvailabilityAsync(
            b.RoomTypeId, DateOnly.FromDateTime(time.ToHotelLocalTime(b.CheckIn)), DateOnly.FromDateTime(time.ToHotelLocalTime(b.CheckOut)), 1);
        Assert.False(availability.Available);
        Assert.Contains(await Service(s => s.GetQueueAsync(fixture.Admin.Id, default)), item => item.BookingCode == b.BookingCode && item.RoomHeld);
    }

    [Fact]
    public async Task Report_requires_secure_access_and_is_idempotent()
    {
        var b = await fixture.CreateBookingAsync();
        var token = await GiveToken(b);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(s => s.ReportAsync(b.BookingCode, "invalid", null, default)));
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Service(s => s.ReportAsync(b.BookingCode, token, null, default))));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "PAYMENT_REPORTED" && a.EntityId == b.Id.ToString())));
    }

    [Fact]
    public async Task Late_report_does_not_reacquire_released_inventory()
    {
        var b = await fixture.CreateBookingAsync(createdAtUtc: DateTime.UtcNow.AddHours(-2));
        var result = await Service(async s => await s.ReportAsync(b.BookingCode, await GiveToken(b), null, default));
        Assert.Equal("Cancelled", result.Status);
        Assert.Equal("PaymentReported", result.PaymentStatus);
        Assert.Contains(await Service(s => s.GetQueueAsync(fixture.Admin.Id, default)), item => item.BookingCode == b.BookingCode && !item.RoomHeld);
    }

    [Fact]
    public async Task Report_survives_concurrent_expiry_without_silently_losing_payment_review()
    {
        var b = await fixture.CreateBookingAsync(createdAtUtc: DateTime.UtcNow.AddHours(-2));
        var token = await GiveToken(b);
        // Owner access survives token revocation by the expiry sweep.
        await fixture.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == fixture.ClientUser.Id)).GuestId = b.GuestId;
            return await db.SaveChangesAsync();
        });
        await Task.WhenAll(Expire(), Service(s => s.ReportAsync(b.BookingCode, token, fixture.ClientUser.Id, default)));
        var result = await fixture.WithDbAsync(db => db.Bookings.AsNoTracking().SingleAsync(x => x.Id == b.Id));
        Assert.Equal(BookingStatus.Cancelled, result.Status);
        Assert.Equal(PaymentStatus.PaymentReported, result.PaymentStatus);
        await fixture.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == fixture.ClientUser.Id)).GuestId = null;
            return await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Manager_can_restore_expired_booking_only_after_credit_and_inventory_checks()
    {
        var b = await fixture.CreateBookingAsync(createdAtUtc: DateTime.UtcNow.AddHours(-2));
        await Expire();
        var result = await Service(s => s.ResolveAsync(b.BookingCode, Request(b), fixture.Manager.Id, default));
        Assert.Equal("Confirmed", result.Status);
        Assert.Equal("Paid", result.PaymentStatus);
        var balance = await fixture.WithDbAsync(async db => FolioAccounting.Calculate(await db.FolioEntries.Where(e => e.Folio!.BookingId == b.Id).ToListAsync()));
        Assert.Equal(0, balance.AmountDue);
        Assert.Equal(0, balance.GuestCredit);
        Assert.Equal(b.Amount, balance.Payments);
    }

    [Fact]
    public async Task Sold_room_cannot_be_restored_and_verified_credit_can_be_queued_for_refund()
    {
        var b = await fixture.CreateBookingAsync(createdAtUtc: DateTime.UtcNow.AddHours(-2));
        await Expire();
        await fixture.WithDbAsync(async db =>
        {
            (await db.Rooms.SingleAsync(r => r.Id == b.RoomId)).IsOnline = false;
            return await db.SaveChangesAsync();
        });
        await Assert.ThrowsAsync<BadRequestException>(() => Service(s => s.ResolveAsync(b.BookingCode, Request(b), fixture.Admin.Id, default)));
        var result = await Service(s => s.ResolveAsync(b.BookingCode, Request(b, "Refund"), fixture.Admin.Id, default));
        Assert.Equal("Cancelled", result.Status);
        Assert.Equal("RefundPending", result.PaymentStatus);
        var balance = await fixture.WithDbAsync(async db => FolioAccounting.Calculate(await db.FolioEntries.Where(e => e.Folio!.BookingId == b.Id).ToListAsync()));
        Assert.Equal(b.Amount, balance.GuestCredit);
        Assert.Equal(0, balance.Refunds);
    }

    [Fact]
    public async Task Concurrent_reviews_record_one_credit_only()
    {
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        var request = Request(b);
        async Task<bool> Attempt()
        {
            try { await Service(s => s.ResolveAsync(b.BookingCode, request, fixture.Admin.Id, default)); return true; }
            catch (BadRequestException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results, x => x);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.FolioEntries.CountAsync(e => e.Folio!.BookingId == b.Id && e.Type == FolioEntryType.Payment)));
    }

    [Fact]
    public async Task Same_bank_credit_cannot_be_used_for_two_different_bookings()
    {
        var a = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        var request = Request(a);
        await Service(s => s.ResolveAsync(a.BookingCode, request, fixture.Admin.Id, default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service(s => s.ResolveAsync(b.BookingCode, request, fixture.Admin.Id, default)));
        Assert.Equal(PaymentStatus.PaymentReported, await fixture.WithDbAsync(db => db.Bookings.Where(x => x.Id == b.Id).Select(x => x.PaymentStatus).SingleAsync()));
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Client")]
    public async Task Non_managers_cannot_review_payments(string role)
    {
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        var actor = role == "Staff" ? fixture.Staff : fixture.ClientUser;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(s => s.ResolveAsync(b.BookingCode, Request(b), actor.Id, default)));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{b.BookingCode}/review-transfer") { Content = JsonContent.Create(Request(b)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reject_requires_review_and_records_no_payment_or_refund()
    {
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        var result = await Service(s => s.ResolveAsync(b.BookingCode, Request(b, "Reject"), fixture.Admin.Id, default));
        Assert.Equal("Cancelled", result.Status);
        Assert.Equal("Unpaid", result.PaymentStatus);
        Assert.Equal(0, await fixture.WithDbAsync(db => db.FolioEntries.CountAsync(e => e.Folio!.BookingId == b.Id && (e.Type == FolioEntryType.Payment || e.Type == FolioEntryType.Refund))));
        await Assert.ThrowsAsync<BadRequestException>(() => Service(s => s.ResolveAsync(b.BookingCode, Request(b, "Reject"), fixture.Admin.Id, default)));
    }

    [Fact]
    public async Task Incorrect_verified_amount_leaves_report_and_folio_untouched()
    {
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        await Assert.ThrowsAsync<BadRequestException>(() => Service(s => s.ResolveAsync(b.BookingCode, Request(b) with { Amount = 1m }, fixture.Admin.Id, default)));
        Assert.Equal(PaymentStatus.PaymentReported, await fixture.WithDbAsync(db => db.Bookings.Where(x => x.Id == b.Id).Select(x => x.PaymentStatus).SingleAsync()));
        Assert.Equal(0, await fixture.WithDbAsync(db => db.FolioEntries.CountAsync(e => e.Folio!.BookingId == b.Id && e.Type == FolioEntryType.Payment)));
    }

    [Fact]
    public async Task Restoration_cannot_take_a_room_assigned_to_another_guest_even_if_type_has_capacity()
    {
        var expired = await fixture.CreateBookingAsync(createdAtUtc: DateTime.UtcNow.AddHours(-2));
        await Expire();
        var replacement = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed, checkInUtc: expired.CheckIn, checkOutUtc: expired.CheckOut);
        var spare = await fixture.CreateRoomAsync();
        await fixture.WithDbAsync(async db =>
        {
            var sold = await db.Bookings.Include(x => x.ReservationRooms).SingleAsync(x => x.Id == replacement.Id);
            sold.RoomId = expired.RoomId;
            sold.RoomTypeId = expired.RoomTypeId;
            foreach (var unit in sold.ReservationRooms) { unit.AssignedRoomId = expired.RoomId; unit.RoomTypeId = expired.RoomTypeId; }
            (await db.Rooms.SingleAsync(r => r.Id == spare.Id)).RoomTypeId = expired.RoomTypeId;
            return await db.SaveChangesAsync();
        });
        await Assert.ThrowsAsync<BadRequestException>(() => Service(s => s.ResolveAsync(expired.BookingCode, Request(expired), fixture.Admin.Id, default)));
        Assert.Equal(BookingStatus.Cancelled, await fixture.WithDbAsync(db => db.Bookings.Where(x => x.Id == expired.Id).Select(x => x.Status).SingleAsync()));
        Assert.Equal(BookingStatus.Confirmed, await fixture.WithDbAsync(db => db.Bookings.Where(x => x.Id == replacement.Id).Select(x => x.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_later_bank_credit_after_rejection_can_still_be_recorded_for_refund()
    {
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        await Service(s => s.ResolveAsync(b.BookingCode, Request(b, "Reject"), fixture.Admin.Id, default));
        var result = await Service(s => s.ResolveAsync(b.BookingCode, Request(b, "Refund"), fixture.Admin.Id, default));
        Assert.Equal("RefundPending", result.PaymentStatus);
        Assert.Equal("Cancelled", result.Status);
    }

    [Fact]
    public async Task Two_bookings_cannot_concurrently_claim_the_same_bank_credit()
    {
        var a = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        var request = Request(a);
        async Task<bool> Attempt(Booking booking)
        {
            try { await Service(s => s.ResolveAsync(booking.BookingCode, request, fixture.Admin.Id, default)); return true; }
            catch (BadRequestException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(a), Attempt(b)), success => success);
    }

    [Fact]
    public async Task Review_requires_exact_acknowledgement_and_cannot_restore_manual_cancellation()
    {
        var b = await fixture.CreateBookingAsync(bookingStatus: BookingStatus.Cancelled);
        await Assert.ThrowsAsync<BadRequestException>(() => Service(s => s.ResolveAsync(b.BookingCode, Request(b) with { ConfirmationText = "verify" }, fixture.Admin.Id, default)));
        await Assert.ThrowsAsync<BadRequestException>(() => Service(s => s.ResolveAsync(b.BookingCode, Request(b), fixture.Admin.Id, default)));
    }

    [Fact]
    public async Task Ordinary_cancellation_and_confirmation_cannot_bypass_payment_review()
    {
        var b = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.PaymentReported);
        foreach (var path in new[] { $"/api/bookings/{b.Id}/cancel", $"/api/bookings/{b.BookingCode}/confirm-transfer" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            { Content = JsonContent.Create(new { reason = "Attempt to bypass review", confirmationText = "ACCEPT" }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Admin.Token);
            request.Headers.Add("X-Moore-App-Environment", "local");
            using var response = await fixture.Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
        }
        Assert.Equal(PaymentStatus.PaymentReported, await fixture.WithDbAsync(db => db.Bookings.Where(x => x.Id == b.Id).Select(x => x.PaymentStatus).SingleAsync()));
    }
}
