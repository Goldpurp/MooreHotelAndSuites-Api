using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

// QA regressions use only the disposable local database and captured email.
[Collection(ManualTransferCollection.Name)]
public sealed class LiveQaDepartureBoundaryTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData(-2)]
    [InlineData(24)]
    public async Task Settled_early_or_late_checkout_closes_once_and_queues_one_cleaning_task(int checkoutOffsetHours)
    {
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.CheckedIn, checkInUtc: DateTime.UtcNow.AddDays(-2),
            checkOutUtc: DateTime.UtcNow.AddHours(checkoutOffsetHours));
        Assert.Equal(HttpStatusCode.OK, await Transition(booking.Id, BookingStatus.CheckedOut));
        Assert.Equal(HttpStatusCode.BadRequest, await Transition(booking.Id, BookingStatus.CheckedOut));
        var stored = await fixture.WithDbAsync(db => db.Bookings.Include(b => b.Folio)
            .Include(b => b.Room).SingleAsync(b => b.Id == booking.Id));
        Assert.Equal(BookingStatus.CheckedOut, stored.Status);
        Assert.Equal(FolioStatus.Closed, stored.Folio!.Status);
        Assert.Equal(RoomStatus.Dirty, stored.Room!.Status);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.HousekeepingTasks.CountAsync(t =>
            t.BookingId == booking.Id && t.Type == HousekeepingTaskType.CheckoutCleaning)));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.EmailOutboxMessages.CountAsync(e =>
            e.DataSubjectGuestId == booking.GuestId && e.Template == "CheckOutThankYou")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkout_rejects_unsettled_charge_or_guest_credit_without_side_effects(bool guestCredit)
    {
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.CheckedIn, checkInUtc: DateTime.UtcNow.AddDays(-1));
        await fixture.WithDbAsync(async db =>
        {
            var folio = await db.Folios.SingleAsync(f => f.BookingId == booking.Id);
            db.FolioEntries.Add(FolioAccounting.NewEntry(folio,
                guestCredit ? FolioEntryType.Credit : FolioEntryType.AddOnCharge,
                guestCredit ? FolioEntryDirection.Credit : FolioEntryDirection.Debit,
                1000m, "Isolated departure QA", "Test", booking.Id.ToString(),
                $"departure-{Guid.NewGuid():N}", DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.BadRequest, await Transition(booking.Id, BookingStatus.CheckedOut));
        var stored = await fixture.WithDbAsync(db => db.Bookings.Include(b => b.Folio).SingleAsync(b => b.Id == booking.Id));
        Assert.Equal(BookingStatus.CheckedIn, stored.Status);
        Assert.Equal(FolioStatus.Open, stored.Folio!.Status);
        Assert.Equal(0, await fixture.WithDbAsync(db => db.HousekeepingTasks.CountAsync(t => t.BookingId == booking.Id)));
        Assert.Equal(0, await fixture.WithDbAsync(db => db.EmailOutboxMessages.CountAsync(e => e.DataSubjectGuestId == booking.GuestId)));
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed, 24)]
    [InlineData(BookingStatus.CheckedIn, -24)]
    [InlineData(BookingStatus.CheckedOut, -24)]
    [InlineData(BookingStatus.Cancelled, -24)]
    public async Task No_show_rejects_future_active_completed_or_cancelled_reservation(BookingStatus initial, int arrivalOffsetHours)
    {
        var booking = await fixture.CreateBookingAsync(bookingStatus: initial,
            checkInUtc: DateTime.UtcNow.AddHours(arrivalOffsetHours), checkOutUtc: DateTime.UtcNow.AddDays(2));
        var response = await Transition(booking.Id, BookingStatus.NoShow);
        var stored = await fixture.WithDbAsync(db => db.Bookings.SingleAsync(b => b.Id == booking.Id));
        Assert.True(response == HttpStatusCode.BadRequest && stored.Status == initial,
            $"Expected safe rejection preserving {initial}; HTTP {(int)response}, stored {stored.Status}.");
        Assert.Equal(0, await fixture.WithDbAsync(db => db.EmailOutboxMessages.CountAsync(e => e.DataSubjectGuestId == booking.GuestId)));
    }

    [Theory]
    [InlineData(RoomStatus.Dirty)]
    [InlineData(RoomStatus.Cleaning)]
    [InlineData(RoomStatus.Clean)]
    [InlineData(RoomStatus.Inspected)]
    [InlineData(RoomStatus.Occupied)]
    [InlineData(RoomStatus.Available)]
    [InlineData(RoomStatus.Maintenance)]
    [InlineData(RoomStatus.OutOfOrder)]
    public async Task No_show_never_releases_an_unclean_or_out_of_service_room(RoomStatus initial)
    {
        var booking = await fixture.CreateBookingAsync(bookingStatus: BookingStatus.Confirmed,
            checkInUtc: DateTime.UtcNow.AddHours(-1));
        await fixture.WithDbAsync(async db =>
        {
            var room = await db.Rooms.SingleAsync(r => r.Id == booking.RoomId);
            room.Status = initial;
            return await db.SaveChangesAsync();
        });
        var response = await Transition(booking.Id, BookingStatus.NoShow);
        Assert.Equal(HttpStatusCode.OK, response);
        Assert.Equal(BookingStatus.NoShow, await fixture.WithDbAsync(db => db.Bookings
            .Where(b => b.Id == booking.Id).Select(b => b.Status).SingleAsync()));
        var status = await fixture.WithDbAsync(db => db.Rooms.Where(r => r.Id == booking.RoomId).Select(r => r.Status).SingleAsync());
        Assert.True(status == initial, $"No-show must preserve room safety; {initial} became {status} (HTTP {(int)response}).");
    }

    [Fact]
    public async Task Repeated_no_show_does_not_queue_duplicate_guest_notices()
    {
        var booking = await fixture.CreateBookingAsync(bookingStatus: BookingStatus.Confirmed,
            checkInUtc: DateTime.UtcNow.AddHours(-1));
        Assert.Equal(HttpStatusCode.OK, await Transition(booking.Id, BookingStatus.NoShow));
        var second = await Transition(booking.Id, BookingStatus.NoShow);
        Assert.True(second is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.Conflict);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.EmailOutboxMessages.CountAsync(e =>
            e.DataSubjectGuestId == booking.GuestId && e.Template == "Cancellation")));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditLogs.CountAsync(a =>
            a.EntityId == booking.Id.ToString() && a.Action == "LIFECYCLE_TRANSITION")));
    }

    private async Task<HttpStatusCode> Transition(Guid bookingId, BookingStatus status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/bookings/{bookingId}/status?status={status}");
        request.Headers.Add("X-Moore-App-Environment", "local");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Admin.Token);
        using var response = await fixture.Client.SendAsync(request);
        return response.StatusCode;
    }
}
