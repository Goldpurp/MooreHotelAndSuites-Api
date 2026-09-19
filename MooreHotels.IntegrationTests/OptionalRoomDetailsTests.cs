using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MooreHotels.Application.DTOs;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class OptionalRoomDetailsTests(ManualTransferTestFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Named_rooms_can_omit_optional_details_and_clear_description()
    {
        var name = $"Named room {Guid.NewGuid():N}";
        using var create = Request(HttpMethod.Post, "/api/rooms", new()
        {
            { new StringContent(name), "Name" },
            { new StringContent("Deluxe"), "Category" },
            { new StringContent("FirstFloor"), "Floor" },
            { new StringContent("Available"), "Status" },
            { new StringContent("35000"), "PricePerNight" },
            { new StringContent("2"), "Capacity" },
            { new StringContent("Wi-Fi"), "Amenities" },
            { new StringContent("true"), "IsOnline" }
        });
        using var created = await fixture.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = await created.Content.ReadFromJsonAsync<RoomDto>(JsonOptions);
        Assert.NotNull(room);
        Assert.Equal(name, room.Name);
        Assert.Equal(string.Empty, room.RoomNumber);
        Assert.Equal(string.Empty, room.Size);
        Assert.Equal(string.Empty, room.Description);

        using var duplicate = Request(HttpMethod.Post, "/api/rooms", new()
        {
            { new StringContent(name.ToUpperInvariant()), "Name" },
            { new StringContent("Deluxe"), "Category" },
            { new StringContent("FirstFloor"), "Floor" },
            { new StringContent("Available"), "Status" },
            { new StringContent("35000"), "PricePerNight" },
            { new StringContent("2"), "Capacity" },
            { new StringContent("Wi-Fi"), "Amenities" }
        });
        using var rejected = await fixture.Client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("room name is already registered", await rejected.Content.ReadAsStringAsync());

        foreach (var description in new[] { "Optional description", "" })
        {
            using var update = Request(HttpMethod.Put, $"/api/rooms/{room.Id}", new()
            {
                { new StringContent(description), "Description" }
            });
            using var updated = await fixture.Client.SendAsync(update);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var get = Request(HttpMethod.Get, $"/api/rooms/{room.Id}");
            using var response = await fixture.Client.SendAsync(get);
            var saved = await response.Content.ReadFromJsonAsync<RoomDto>(JsonOptions);
            Assert.NotNull(saved);
            Assert.Equal(description, saved.Description);
            Assert.Equal(room.Id, saved.Id);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, MultipartFormDataContent? form = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Admin.Token);
        return request;
    }
}
