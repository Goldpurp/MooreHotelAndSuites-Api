using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class SensitiveTokenTransportTests
{
    private readonly ManualTransferTestFixture _fixture;

    public SensitiveTokenTransportTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Email_verification_rejects_query_tokens_and_accepts_the_request_body()
    {
        var userId = Guid.NewGuid();
        var email = $"verify-{userId:N}@example.test";
        string encodedToken;

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = userId,
                UserName = email,
                Email = email,
                Name = "Verification Transport Test",
                Role = UserRole.Client,
                Status = ProfileStatus.Active,
                EmailConfirmed = false,
                LockoutEnabled = true,
                CreatedAt = DateTime.UtcNow
            };
            var created = await userManager.CreateAsync(user, "TokenFlow123!");
            Assert.True(created.Succeeded);
            encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
                await userManager.GenerateEmailConfirmationTokenAsync(user)));
        }

        try
        {
            using (var queryResponse = await _fixture.Client.GetAsync(
                       $"/api/auth/verify-email?userId={userId}&token={Uri.EscapeDataString(encodedToken)}"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, queryResponse.StatusCode);
            }

            using var bodyResponse = await _fixture.Client.PostAsJsonAsync(
                "/api/auth/verify-email",
                new { userId, token = encodedToken });
            Assert.Equal(HttpStatusCode.OK, bodyResponse.StatusCode);
            Assert.True(bodyResponse.Headers.CacheControl?.NoStore);

            await using var verificationScope = _fixture.Services.CreateAsyncScope();
            var verificationManager = verificationScope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await verificationManager.FindByIdAsync(userId.ToString()))!.EmailConfirmed);
        }
        finally
        {
            await using var cleanupScope = _fixture.Services.CreateAsyncScope();
            var cleanupManager = cleanupScope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            var user = await cleanupManager.FindByIdAsync(userId.ToString());
            if (user is not null)
                await cleanupManager.DeleteAsync(user);
        }
    }

    [Fact]
    public async Task Realtime_uses_a_single_use_opaque_ticket_instead_of_a_JWT_query_parameter()
    {
        using (var clientRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   "/api/notifications/realtime-ticket"))
        {
            clientRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _fixture.ClientUser.Token);
            using var clientResponse = await _fixture.Client.SendAsync(clientRequest);
            Assert.Equal(HttpStatusCode.Forbidden, clientResponse.StatusCode);
        }

        using var issueRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/notifications/realtime-ticket");
        issueRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _fixture.Admin.Token);
        using var issueResponse = await _fixture.Client.SendAsync(issueRequest);

        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        Assert.True(issueResponse.Headers.CacheControl?.NoStore);
        using var payload = JsonDocument.Parse(await issueResponse.Content.ReadAsStringAsync());
        var ticket = payload.RootElement.GetProperty("ticket").GetString();
        Assert.NotNull(ticket);
        Assert.Equal(43, ticket.Length);
        Assert.DoesNotContain(_fixture.Admin.Token, await issueResponse.Content.ReadAsStringAsync());
        Assert.Equal("WebSockets", payload.RootElement.GetProperty("transport").GetString());
        Assert.True(payload.RootElement.GetProperty("skipNegotiation").GetBoolean());

        var store = _fixture.Services.GetRequiredService<RealtimeAccessTicketStore>();
        Assert.True(store.TryConsume(ticket, out var bearerToken));
        Assert.Equal(_fixture.Admin.Token, bearerToken);
        Assert.False(store.TryConsume(ticket, out _));

        using var directJwtResponse = await _fixture.Client.GetAsync(
            $"/hubs/notifications?access_token={Uri.EscapeDataString(_fixture.Admin.Token)}");
        Assert.Equal(HttpStatusCode.Unauthorized, directJwtResponse.StatusCode);
    }
}

public sealed class RealtimeAccessTicketStoreTests
{
    [Fact]
    public void Expired_or_non_ticket_values_cannot_be_consumed()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new RealtimeAccessTicketStore(time);
        var ticket = store.Issue("header.payload.signature");

        Assert.False(store.TryConsume("header.payload.signature", out _));
        time.Advance(RealtimeAccessTicketStore.TicketLifetime);
        Assert.False(store.TryConsume(ticket.Ticket, out _));
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
