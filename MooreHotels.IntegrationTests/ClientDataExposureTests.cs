using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Domain.Common;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class ClientDataExposureTests
{
    private readonly ManualTransferTestFixture _fixture;

    public ClientDataExposureTests(ManualTransferTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Client_booking_history_excludes_staff_and_provider_only_fields()
    {
        var booking = await _fixture.CreateBookingAsync();
        await _fixture.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(item => item.Id == _fixture.ClientUser.Id);
            user.GuestId = booking.GuestId;

            var stored = await db.Bookings
                .Include(item => item.ReservationRooms)
                .SingleAsync(item => item.Id == booking.Id);
            stored.ReservationRooms.Single().AssignedByUserId = _fixture.Admin.Id;
            await db.SaveChangesAsync();
            return true;
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile/bookings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.ClientUser.Token);
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = document.RootElement.EnumerateArray().Single();
        Assert.False(item.TryGetProperty("guestId", out _));
        Assert.False(item.TryGetProperty("guestPhone", out _));
        Assert.False(item.TryGetProperty("transactionReference", out _));
        Assert.False(item.TryGetProperty("refundApprovedByUserId", out _));
        Assert.False(item.TryGetProperty("refundProcessedByUserId", out _));
        Assert.False(item.GetProperty("rooms")[0].TryGetProperty("assignedByUserId", out _));
    }

    [Fact]
    public async Task Secure_guest_lookup_excludes_room_assignment_actor()
    {
        const string accessToken = "guest-access-token-used-only-by-this-test";
        var booking = await _fixture.CreateBookingAsync();
        await _fixture.WithDbAsync(async db =>
        {
            var stored = await db.Bookings
                .Include(item => item.ReservationRooms)
                .SingleAsync(item => item.Id == booking.Id);
            stored.GuestAccessTokenHash = BookingGuestAccess.Hash(accessToken);
            stored.GuestAccessTokenIssuedAtUtc = DateTime.UtcNow;
            stored.GuestAccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(10);
            stored.ReservationRooms.Single().AssignedByUserId = _fixture.Admin.Id;
            await db.SaveChangesAsync();
            return true;
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings/lookup")
        {
            Content = JsonContent.Create(new { code = booking.BookingCode })
        };
        request.Headers.Add("X-Booking-Access-Token", accessToken);
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("rooms")[0]
            .TryGetProperty("assignedByUserId", out _));
    }
}
