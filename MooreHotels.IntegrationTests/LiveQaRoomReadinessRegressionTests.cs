using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

// Regression specification for live finding QA-23.
[Collection(ManualTransferCollection.Name)]
public sealed class LiveQaRoomReadinessRegressionTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData(RoomStatus.Available, false, true)]
    [InlineData(RoomStatus.Available, true, true)]
    // Inspection in progress is not released inventory; completion sets Available.
    [InlineData(RoomStatus.Inspected, false, false)]
    [InlineData(RoomStatus.Inspected, true, false)]
    [InlineData(RoomStatus.Dirty, false, false)]
    [InlineData(RoomStatus.Dirty, true, false)]
    [InlineData(RoomStatus.Cleaning, false, false)]
    [InlineData(RoomStatus.Cleaning, true, false)]
    [InlineData(RoomStatus.Clean, false, false)]
    [InlineData(RoomStatus.Clean, true, false)]
    public async Task Same_day_public_inventory_respects_room_readiness(
        RoomStatus status, bool useSearch, bool expectedAvailable)
    {
        var room = await fixture.CreateRoomAsync();
        await fixture.WithDbAsync(async db =>
        {
            var stored = await db.Rooms.SingleAsync(item => item.Id == room.Id);
            stored.Status = status;
            stored.IsOnline = true;
            return await db.SaveChangesAsync();
        });

        await using var scope = fixture.Services.CreateAsyncScope();
        var today = scope.ServiceProvider.GetRequiredService<IHotelTimeService>().Today;
        var dates = $"checkIn={today:yyyy-MM-dd}&checkOut={today.AddDays(1):yyyy-MM-dd}";
        var path = useSearch
            ? $"/api/rooms/search?{dates}&guest=1"
            : $"/api/rooms/{room.Id}/availability?{dates}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Moore-App-Environment", "local");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var actualAvailable = useSearch
            ? json.RootElement.EnumerateArray().Any(item => item.GetProperty("id").GetGuid() == room.Id)
            : json.RootElement.GetProperty("available").GetBoolean();
        Assert.Equal(expectedAvailable, actualAvailable);
    }
}
