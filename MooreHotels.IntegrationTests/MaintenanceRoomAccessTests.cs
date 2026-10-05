using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class MaintenanceRoomAccessTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData("Engineering", true)]
    [InlineData("Maintenance", true)]
    [InlineData("Reception", false)]
    [InlineData("Finance", false)]
    [InlineData("Housekeeping", false)]
    public async Task Room_picker_only_exposes_operational_room_fields_to_maintenance_staff(string department, bool allowed)
    {
        var user = await fixture.CreateUserAsync(UserRole.Staff, department);
        await fixture.CreateRoomAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/maintenance/rooms");
        request.Headers.Add("X-Moore-App-Environment", "local");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        if (!allowed) return;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEmpty(json.RootElement.EnumerateArray());
        foreach (var room in json.RootElement.EnumerateArray())
        {
            Assert.Equal(new[] { "id", "name", "roomNumber", "status" },
                room.EnumerateObject().Select(p => p.Name).OrderBy(p => p));
        }
    }
}
