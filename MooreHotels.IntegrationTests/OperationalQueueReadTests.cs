using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class OperationalQueueReadTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task Channel_events_are_paginated_scoped_and_omit_raw_payloads()
    {
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IChannelManagementService>();
        var channel = await service.SaveChannelAsync(null, new SaveDistributionChannelRequest("QUEUE" + Guid.NewGuid().ToString("N")[..10], "QA queue", true), fixture.Admin.Id);
        for (var i = 0; i < 3; i++)
            await service.ReceiveEventAsync(channel.Code, new ReceiveChannelEventRequest("RESERVATION_CREATED", $"qa-event-key-{i}", $"external-{i}", "{\"private\":\"do-not-list\"}", null));
        var first = await Read($"/api/channels/{channel.Id}/events?page=1&pageSize=2", fixture.Admin.Token);
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        var second = await Read($"/api/channels/{channel.Id}/events?page=2&pageSize=2", fixture.Admin.Token);
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("do-not-list", first.GetRawText());
        Assert.DoesNotContain("payloadJson", first.GetRawText());
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("id").GetString(), second.GetProperty("items")[0].GetProperty("id").GetString());
        using var denied = await Get($"/api/channels/{channel.Id}/events", fixture.Staff.Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Retry_pages_hide_email_contents_and_distinguish_quarantined_jobs()
    {
        var id = Guid.NewGuid();
        await fixture.WithDbAsync(async db =>
        {
            db.EmailOutboxMessages.Add(new EmailOutboxMessage { Id = id, Template = "QAQuarantined", Recipient = "private-queue@example.test", ProtectedPayload = "private-payload", AttemptCount = 12, CreatedAtUtc = DateTime.UtcNow.AddYears(-10), NextAttemptAtUtc = DateTime.UtcNow, QuarantinedAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        try
        {
            var result = await Read("/api/operations/email-outbox/dead-letters/page?page=1&pageSize=1", fixture.Admin.Token);
            Assert.Equal(id, result.GetProperty("items")[0].GetProperty("id").GetGuid());
            Assert.False(result.GetProperty("items")[0].GetProperty("canRetry").GetBoolean());
            Assert.DoesNotContain("private-queue", result.GetRawText());
            Assert.DoesNotContain("private-payload", result.GetRawText());
            using var denied = await Get("/api/operations/email-outbox/dead-letters/page", fixture.Manager.Token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var media = await Get("/api/admin/media-deletions/failed/page", fixture.Manager.Token);
            Assert.Equal(HttpStatusCode.OK, media.StatusCode);
            using var staffMedia = await Get("/api/admin/media-deletions/failed/page", fixture.Staff.Token);
            Assert.Equal(HttpStatusCode.Forbidden, staffMedia.StatusCode);
        }
        finally
        {
            await fixture.WithDbAsync(async db =>
            {
                var row = await db.EmailOutboxMessages.FindAsync(id);
                if (row != null) db.EmailOutboxMessages.Remove(row);
                return await db.SaveChangesAsync();
            });
        }
    }

    private async Task<HttpResponseMessage> Get(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return await fixture.Client.SendAsync(request);
    }
    private async Task<JsonElement> Read(string path, string token)
    {
        using var response = await Get(path, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
