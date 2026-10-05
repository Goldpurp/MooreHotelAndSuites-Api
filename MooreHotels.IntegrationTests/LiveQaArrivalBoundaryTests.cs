using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class LiveQaArrivalBoundaryTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData(RoomStatus.Available, true, true)]
    [InlineData(RoomStatus.Available, false, false)]
    [InlineData(RoomStatus.Dirty, true, false)]
    [InlineData(RoomStatus.Cleaning, true, false)]
    [InlineData(RoomStatus.Clean, true, false)]
    [InlineData(RoomStatus.Inspected, true, false)]
    [InlineData(RoomStatus.Maintenance, true, false)]
    [InlineData(RoomStatus.OutOfOrder, true, false)]
    public async Task Checkin_requires_online_available_room_even_when_payment_is_complete(
        RoomStatus status, bool online, bool permitted)
    {
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed, checkInUtc: DateTime.UtcNow.AddHours(-1),
            checkOutUtc: DateTime.UtcNow.AddDays(1));
        await fixture.WithDbAsync(async db =>
        {
            var room = await db.Rooms.SingleAsync(r => r.Id == booking.RoomId);
            room.Status = status;
            room.IsOnline = online;
            return await db.SaveChangesAsync();
        });
        using var response = await CheckIn(booking.Id);
        Assert.Equal(permitted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(permitted ? BookingStatus.CheckedIn : BookingStatus.Confirmed,
            await fixture.WithDbAsync(db => db.Bookings.Where(b => b.Id == booking.Id).Select(b => b.Status).SingleAsync()));
        Assert.Equal(permitted ? RoomStatus.Occupied : status,
            await fixture.WithDbAsync(db => db.Rooms.Where(r => r.Id == booking.RoomId).Select(r => r.Status).SingleAsync()));
    }

    [Theory]
    [InlineData(60, 1440)]
    [InlineData(-1440, -5)]
    [InlineData(-60, 10)]
    public async Task Checkin_rejects_early_past_and_too_close_to_checkout(int arrivalOffsetMinutes, int departureOffsetMinutes)
    {
        var now = DateTime.UtcNow;
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed, checkInUtc: now.AddMinutes(arrivalOffsetMinutes),
            checkOutUtc: now.AddMinutes(departureOffsetMinutes));
        using var response = await CheckIn(booking.Id);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BookingStatus.Confirmed, await fixture.WithDbAsync(db => db.Bookings
            .Where(b => b.Id == booking.Id).Select(b => b.Status).SingleAsync()));
    }

    private async Task<HttpResponseMessage> CheckIn(Guid bookingId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/bookings/{bookingId}/status?status=CheckedIn");
        request.Headers.Add("X-Moore-App-Environment", "local");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Admin.Token);
        return await fixture.Client.SendAsync(request);
    }
}
