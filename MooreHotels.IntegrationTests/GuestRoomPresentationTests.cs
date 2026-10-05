using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class GuestRoomPresentationTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData(BookingStatus.Confirmed, true, "Assigned")]
    [InlineData(BookingStatus.Pending, false, "Pending")]
    [InlineData(BookingStatus.Cancelled, true, "Released")]
    [InlineData(BookingStatus.NoShow, true, "Released")]
    [InlineData(BookingStatus.CheckedOut, true, "Completed")]
    public async Task Guest_lookup_exposes_safe_room_display_without_staff_assignment_metadata(
        BookingStatus status, bool assigned, string expectedState)
    {
        var booking = await fixture.CreateBookingAsync(bookingStatus: status);
        var roomName = "QA suite " + booking.Id.ToString("N");
        var email = await fixture.WithDbAsync(async db =>
        {
            var stored = await db.Bookings.Include(b => b.Guest)
                .Include(b => b.ReservationRooms).ThenInclude(r => r.AssignedRoom)
                .SingleAsync(b => b.Id == booking.Id);
            var unit = Assert.Single(stored.ReservationRooms);
            unit.AssignedRoom!.Name = roomName;
            unit.AssignedByUserId = fixture.Admin.Id;
            if (!assigned) unit.AssignedRoomId = null;
            await db.SaveChangesAsync();
            return stored.Guest!.Email;
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings/lookup");
        request.Headers.Add("X-Moore-App-Environment", "local");
        request.Content = JsonContent.Create(new { code = booking.BookingCode, email });
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var room = Assert.Single(json.RootElement.GetProperty("rooms").EnumerateArray());
        Assert.Equal(expectedState, room.GetProperty("assignmentStatus").GetString());
        Assert.Equal(assigned ? roomName : null, room.GetProperty("assignedRoomName").GetString());
        Assert.False(room.TryGetProperty("assignedByUserId", out _));
        Assert.False(room.TryGetProperty("assignedRoomId", out _));
        Assert.False(room.TryGetProperty("assignedRoomNumber", out _));
    }
}
