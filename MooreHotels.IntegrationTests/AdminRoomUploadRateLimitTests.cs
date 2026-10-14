using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class AdminRoomUploadRateLimitTests : IAsyncLifetime
{
    // Exercise the real middleware and controllers with limits enabled, using
    // an isolated local database and local image storage only.
    private readonly ManualTransferTestFixture _fixture = new() { EnableRateLimiting = true };

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Admin_can_create_edit_and_upload_photos_for_more_than_ten_rooms()
    {
        for (var index = 0; index < 12; index++)
        {
            using var create = RoomForm();
            using var created = await SendAsync(HttpMethod.Post, "/api/rooms", _fixture.Admin, create);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var room = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = room.RootElement.GetProperty("id").GetGuid();

            using var update = new MultipartFormDataContent
            {
                { new StringContent("Updated during hotel setup"), "Description" }
            };
            using var updated = await SendAsync(HttpMethod.Put, $"/api/rooms/{id}", _fixture.Admin, update);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

            using var photos = PhotoForm();
            using var uploaded = await SendAsync(HttpMethod.Post, $"/api/rooms/{id}/images", _fixture.Admin, photos);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }

        using var invalid = RoomForm();
        invalid.Add(new StringContent("not an image"), "files", "invalid.png");
        using var rejected = await SendAsync(HttpMethod.Post, "/api/rooms", _fixture.Admin, invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Managers_keep_hourly_limit_and_other_roles_cannot_write_rooms()
    {
        for (var index = 0; index < 10; index++)
        {
            using var form = RoomForm();
            using var response = await SendAsync(HttpMethod.Post, "/api/rooms", _fixture.Manager, form);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var blockedForm = RoomForm();
        using var blocked = await SendAsync(HttpMethod.Post, "/api/rooms", _fixture.Manager, blockedForm);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        using var adminForm = RoomForm();
        using var admin = await SendAsync(HttpMethod.Post, "/api/rooms", _fixture.Admin, adminForm);
        Assert.Equal(HttpStatusCode.Created, admin.StatusCode);

        foreach (var user in new[] { _fixture.Staff, _fixture.ClientUser })
        {
            using var form = RoomForm();
            using var response = await SendAsync(HttpMethod.Post, "/api/rooms", user, form);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var anonymousForm = RoomForm();
        using var anonymous = await _fixture.Client.PostAsync("/api/rooms", anonymousForm);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Admin_exemption_does_not_apply_to_general_images_or_authentication()
    {
        for (var index = 0; index < 10; index++)
        {
            using var form = PhotoForm("file");
            using var response = await SendAsync(HttpMethod.Post, "/api/images/upload", _fixture.Admin, form);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var blockedForm = PhotoForm("file");
        using var blocked = await SendAsync(HttpMethod.Post, "/api/images/upload", _fixture.Admin, blockedForm);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        for (var index = 0; index < 10; index++)
        {
            using var body = JsonContent.Create(new { email = "invalid", password = "invalid" });
            using var response = await SendAsync(HttpMethod.Post, "/api/auth/login", _fixture.Admin, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var loginBody = JsonContent.Create(new { email = "invalid", password = "invalid" });
        using var login = await SendAsync(HttpMethod.Post, "/api/auth/login", _fixture.Admin, loginBody);
        Assert.Equal(HttpStatusCode.TooManyRequests, login.StatusCode);

        using var roomForm = RoomForm();
        using var room = await SendAsync(HttpMethod.Post, "/api/rooms", _fixture.Admin, roomForm);
        Assert.Equal(HttpStatusCode.Created, room.StatusCode);
    }

    [Fact]
    public async Task Admin_room_writes_still_obey_the_global_traffic_limit()
    {
        for (var index = 0; index < 300; index++)
        {
            using var response = await SendAsync(HttpMethod.Get, "/health/live", _fixture.Admin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var form = RoomForm();
        using var blocked = await SendAsync(HttpMethod.Post, "/api/rooms", _fixture.Admin, form);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, TestUser user, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return await _fixture.Client.SendAsync(request);
    }

    private static MultipartFormDataContent RoomForm() => new()
    {
        { new StringContent($"Setup {Guid.NewGuid():N}"), "Name" },
        { new StringContent("Deluxe"), "Category" },
        { new StringContent("FirstFloor"), "Floor" },
        { new StringContent("Available"), "Status" },
        { new StringContent("35000"), "PricePerNight" },
        { new StringContent("2"), "Capacity" },
        { new StringContent("Wi-Fi"), "Amenities" }
    };

    private static MultipartFormDataContent PhotoForm(string field = "files")
    {
        var image = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { image, field, "room.png" } };
    }
}
