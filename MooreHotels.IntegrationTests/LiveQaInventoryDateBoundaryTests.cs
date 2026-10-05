using System.Net;
using System.Text.Json;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class LiveQaInventoryDateBoundaryTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData(-2, 0, true)]
    [InlineData(2, 4, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(-1, 1, false)]
    [InlineData(1, 3, false)]
    [InlineData(-1, 3, false)]
    public async Task Physical_room_and_type_inventory_agree_on_half_open_stay_dates(int from, int until, bool expected)
    {
        var day = DateTime.UtcNow.Date.AddDays(60);
        var booking = await fixture.CreateBookingAsync(paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed, checkInUtc: day.AddHours(13), checkOutUtc: day.AddDays(2).AddHours(11));
        var query = $"checkIn={day.AddDays(from):yyyy-MM-dd}&checkOut={day.AddDays(until):yyyy-MM-dd}";
        foreach (var path in new[]
        {
            $"/api/rooms/{booking.RoomId}/availability?{query}",
            $"/api/inventory/room-types/{booking.RoomTypeId}/availability?{query}&units=1"
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Moore-App-Environment", "local");
            using var response = await fixture.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {text}");
            using var json = JsonDocument.Parse(text);
            Assert.True(json.RootElement.GetProperty("available").GetBoolean() == expected,
                $"{path}: expected availability={expected}; body={text}");
        }
    }
}
