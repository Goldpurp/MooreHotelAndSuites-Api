using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

// Full business-operation transitions against disposable PostgreSQL, not production.
[Collection(ManualTransferCollection.Name)]
public sealed class LiveQaBusinessOperationsTests(ManualTransferTestFixture fixture)
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    [Fact]
    public async Task Rate_plan_daily_rate_rule_and_promotion_changes_reconcile_quotes_and_audit()
    {
        var room = await fixture.CreateRoomAsync();
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(55));
        var code = "QA" + Guid.NewGuid().ToString("N")[..12];
        var planBody = new RatePlanRequest(code, "QA business plan", null, "NGN", RateAdjustmentType.None,
            0, 1, 90, null, null, false, true);
        var plan = await Ok(HttpMethod.Post, "/api/pricing/rate-plans", planBody);
        var planId = plan.GetProperty("id").GetGuid();
        var dailyBody = new DailyRoomRateRequest(planId, room.Id, null, day, 12000.25m);
        var daily = await Ok(HttpMethod.Post, "/api/pricing/daily-rates", dailyBody);
        var dailyId = daily.GetProperty("id").GetGuid();
        async Task<JsonElement> Quote(string? promo = null) => await Ok(HttpMethod.Post, "/api/pricing/quotes",
            new CreatePricingQuoteRequest(room.Id, DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                DateTime.SpecifyKind(day.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc), 1, 0, code, promo), false);
        Assert.Equal(12000.25m, (await Quote()).GetProperty("totalAmount").GetDecimal());
        await Ok(HttpMethod.Put, $"/api/pricing/daily-rates/{dailyId}", dailyBody with { Amount = 16000m });
        Assert.Equal(16000m, (await Quote()).GetProperty("totalAmount").GetDecimal());
        var promoBody = new PromotionRequest(code + "P", "QA ten percent", DiscountType.Percentage, 10,
            1000m, "NGN", 1, planId, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(2), 2, true);
        var promo = await Ok(HttpMethod.Post, "/api/pricing/promotions", promoBody);
        var promoId = promo.GetProperty("id").GetGuid();
        var discounted = await Quote(code + "P");
        Assert.Equal(1000m, discounted.GetProperty("discountAmount").GetDecimal());
        Assert.Equal(15000m, discounted.GetProperty("totalAmount").GetDecimal());
        await Ok(HttpMethod.Put, $"/api/pricing/promotions/{promoId}", promoBody with { IsActive = false });
        var rejected = await Send(HttpMethod.Post, "/api/pricing/quotes",
            new CreatePricingQuoteRequest(room.Id, day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue), 1, 0, code, code + "P"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
        var ruleBody = new PricingRuleRequest(code + "F", "QA fee", PricingRuleKind.Fee,
            PricingRuleCalculation.FixedPerStay, 250m, "NGN", false, day, day, 100, true);
        var rule = await Ok(HttpMethod.Post, "/api/pricing/rules", ruleBody);
        Assert.Equal(16250m, (await Quote()).GetProperty("totalAmount").GetDecimal());
        await Ok(HttpMethod.Put, $"/api/pricing/rules/{rule.GetProperty("id").GetGuid()}", ruleBody with { IsActive = false });
        Assert.Equal(16000m, (await Quote()).GetProperty("totalAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/pricing/daily-rates/{dailyId}", null, fixture.Admin)).Status);
        Assert.Equal(75000m, (await Quote()).GetProperty("totalAmount").GetDecimal());
        await Ok(HttpMethod.Put, $"/api/pricing/rate-plans/{planId}", planBody with { IsActive = false });
        var config = await Ok(HttpMethod.Get, "/api/pricing/configuration");
        Assert.Contains(config.GetProperty("ratePlans").EnumerateArray(), p => p.GetProperty("id").GetGuid() == planId && !p.GetProperty("isActive").GetBoolean());
        Assert.True(await fixture.WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityId == planId.ToString() && a.ProfileId == fixture.Admin.Id)));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("exhausted")]
    [InlineData("minimum-nights")]
    [InlineData("different-plan")]
    public async Task Promotion_eligibility_is_enforced_at_quote(string scenario)
    {
        var room = await fixture.CreateRoomAsync();
        var code = "QA" + Guid.NewGuid().ToString("N")[..12];
        var plan = await Ok(HttpMethod.Post, "/api/pricing/rate-plans", new RatePlanRequest(code,
            "Promotion-only plan", null, "NGN", RateAdjustmentType.None, 0, 1, 90, null, null, false, true));
        var body = new PromotionRequest(code + "P", "QA eligibility", DiscountType.Percentage, 10, null,
            "NGN", scenario == "minimum-nights" ? 3 : 1,
            scenario == "different-plan" ? plan.GetProperty("id").GetGuid() : null,
            DateTime.UtcNow.AddDays(scenario == "future" ? 1 : -3),
            DateTime.UtcNow.AddDays(scenario == "expired" ? -1 : 3), 1, true);
        var promo = await Ok(HttpMethod.Post, "/api/pricing/promotions", body);
        if (scenario == "exhausted") await fixture.WithDbAsync(async db =>
        {
            (await db.Promotions.SingleAsync(p => p.Id == promo.GetProperty("id").GetGuid())).RedemptionCount = 1;
            return await db.SaveChangesAsync();
        });
        var before = await fixture.WithDbAsync(db => db.BookingQuotes.CountAsync(q => q.RoomId == room.Id));
        var result = await Send(HttpMethod.Post, "/api/pricing/quotes", new CreatePricingQuoteRequest(room.Id,
            DateTime.UtcNow.Date.AddDays(50), DateTime.UtcNow.Date.AddDays(51), 1, 0, null, body.Code));
        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal(before, await fixture.WithDbAsync(db => db.BookingQuotes.CountAsync(q => q.RoomId == room.Id)));
    }

    [Fact]
    public async Task Channel_create_update_event_retry_inventory_and_disable_are_consistent()
    {
        var booking = await fixture.CreateBookingAsync();
        var code = "QA" + Guid.NewGuid().ToString("N")[..12];
        var channelBody = new SaveDistributionChannelRequest(code, "QA channel", true);
        var channel = await Ok(HttpMethod.Post, "/api/channels", channelBody);
        var id = channel.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, "/api/channels", channelBody, fixture.Admin)).Status);
        var evBody = new ReceiveChannelEventRequest("RESERVATION_CREATED", "qa-key-" + code, "EXT-" + code, "{\"guests\":1}", null);
        var ev = await Ok(HttpMethod.Post, $"/api/channels/{code}/events/inbound", evBody);
        var evId = ev.GetProperty("id").GetGuid();
        Assert.Equal(evId, (await Ok(HttpMethod.Post, $"/api/channels/{code}/events/inbound", evBody)).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/channels/{code}/events/inbound", evBody with { PayloadJson = "{\"guests\":2}" }, fixture.Admin)).Status);
        var reconciliation = await Ok(HttpMethod.Get, $"/api/channels/{id}/reconciliation");
        Assert.Equal(1, reconciliation.GetProperty("unmappedReservationEvents").GetInt32());
        await Ok(HttpMethod.Put, $"/api/channels/events/{evId}", new UpdateChannelEventRequest(ChannelEventStatus.Failed, "QA simulated failure"));
        Assert.Equal(1, (await Ok(HttpMethod.Get, $"/api/channels/{id}/reconciliation")).GetProperty("failedEvents").GetInt32());
        await Ok(HttpMethod.Put, $"/api/channels/events/{evId}", new UpdateChannelEventRequest(ChannelEventStatus.Pending, null));
        var processed = await Ok(HttpMethod.Put, $"/api/channels/events/{evId}", new UpdateChannelEventRequest(ChannelEventStatus.Processed, null));
        var repeated = await Ok(HttpMethod.Put, $"/api/channels/events/{evId}", new UpdateChannelEventRequest(ChannelEventStatus.Processed, null));
        Assert.Equal(processed.GetProperty("attemptCount").GetInt32(), repeated.GetProperty("attemptCount").GetInt32());
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60));
        var queue = new QueueChannelInventoryRequest(booking.RoomTypeId, day, day.AddDays(1), "qa-inventory-" + code);
        var inventory = await Ok(HttpMethod.Post, $"/api/channels/{id}/inventory-events", queue);
        Assert.Equal(inventory.GetProperty("id").GetGuid(), (await Ok(HttpMethod.Post, $"/api/channels/{id}/inventory-events", queue)).GetProperty("id").GetGuid());
        using var payload = JsonDocument.Parse(inventory.GetProperty("payloadJson").GetString()!);
        Assert.Equal(2, payload.RootElement.GetProperty("Days").GetArrayLength());
        await Ok(HttpMethod.Put, $"/api/channels/{id}", channelBody with { Name = "QA disabled channel", IsActive = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/channels/{code}/events/inbound", evBody with { IdempotencyKey = "qa-new-" + code }, fixture.Admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/channels/{id}/inventory-events", queue with { IdempotencyKey = "qa-disabled-" + code }, fixture.Admin)).Status);
    }

    [Fact]
    public async Task Channel_reservation_mapping_links_existing_booking_once_without_reinserting_it()
    {
        var booking = await fixture.CreateBookingAsync();
        var code = "QA" + Guid.NewGuid().ToString("N")[..12];
        var channel = await Ok(HttpMethod.Post, "/api/channels", new SaveDistributionChannelRequest(code, "QA mapping channel", true));
        var id = channel.GetProperty("id").GetGuid();
        var ext = "EXT-" + code;
        await Ok(HttpMethod.Post, $"/api/channels/{code}/events/inbound",
            new ReceiveChannelEventRequest("RESERVATION_CREATED", "qa-mapping-" + code, ext, "{}", null));
        var link = new LinkChannelReservationRequest(ext, booking.Id);
        var mapping = await Ok(HttpMethod.Post, $"/api/channels/{id}/reservation-mappings", link);
        Assert.Equal(booking.BookingCode, mapping.GetProperty("bookingCode").GetString());
        Assert.Equal(mapping.GetProperty("id").GetGuid(), (await Ok(HttpMethod.Post, $"/api/channels/{id}/reservation-mappings", link)).GetProperty("id").GetGuid());
        Assert.Equal(0, (await Ok(HttpMethod.Get, $"/api/channels/{id}/reconciliation")).GetProperty("unmappedReservationEvents").GetInt32());
        Assert.Equal(1, await fixture.WithDbAsync(db => db.Bookings.CountAsync(b => b.Id == booking.Id)));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.ChannelReservationMappings.CountAsync(m => m.ChannelId == id)));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditLogs.CountAsync(a =>
            a.EntityId == mapping.GetProperty("id").GetGuid().ToString() && a.Action == "CHANNEL_RESERVATION_LINKED")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Channel_mapping_rejects_a_different_external_id_or_booking_without_replacing_the_link(bool changeBooking)
    {
        var booking = await fixture.CreateBookingAsync();
        var other = await fixture.CreateBookingAsync();
        var code = "QA" + Guid.NewGuid().ToString("N")[..12];
        var channel = await Ok(HttpMethod.Post, "/api/channels", new SaveDistributionChannelRequest(code, "QA uniqueness channel", true));
        var id = channel.GetProperty("id").GetGuid();
        var link = new LinkChannelReservationRequest("EXT-" + code, booking.Id);
        var mapping = await Ok(HttpMethod.Post, $"/api/channels/{id}/reservation-mappings", link);
        var conflict = changeBooking ? link with { BookingId = other.Id } : link with { ExternalReservationId = "OTHER-" + code };
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/channels/{id}/reservation-mappings", conflict, fixture.Admin)).Status);
        var stored = await fixture.WithDbAsync(db => db.ChannelReservationMappings.SingleAsync(m => m.ChannelId == id));
        Assert.Equal(mapping.GetProperty("id").GetGuid(), stored.Id);
        Assert.Equal(booking.Id, stored.BookingId);
        Assert.Equal(link.ExternalReservationId, stored.ExternalReservationId);
    }

    [Fact]
    public async Task Concurrent_channel_mapping_replay_creates_only_one_link_and_audit_entry()
    {
        var booking = await fixture.CreateBookingAsync();
        var code = "QA" + Guid.NewGuid().ToString("N")[..12];
        var channel = await Ok(HttpMethod.Post, "/api/channels", new SaveDistributionChannelRequest(code, "QA concurrent channel", true));
        var id = channel.GetProperty("id").GetGuid();
        var link = new LinkChannelReservationRequest("EXT-" + code, booking.Id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Ok(HttpMethod.Post, $"/api/channels/{id}/reservation-mappings", link)));
        var mappingId = responses[0].GetProperty("id").GetGuid();
        Assert.All(responses, response => Assert.Equal(mappingId, response.GetProperty("id").GetGuid()));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.ChannelReservationMappings.CountAsync(m => m.ChannelId == id)));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditLogs.CountAsync(a =>
            a.EntityId == mappingId.ToString() && a.Action == "CHANNEL_RESERVATION_LINKED")));
    }

    [Theory]
    [InlineData("/api/profile/me")]
    [InlineData("/api/bookings")]
    [InlineData("/api/rooms")]
    [InlineData("/api/housekeeping/tasks")]
    [InlineData("/api/operations/board")]
    public async Task Suspended_account_cannot_use_previous_token_on_protected_operations(string path)
    {
        var actor = await fixture.CreateUserAsync(UserRole.Manager);
        await fixture.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == actor.Id)).Status = ProfileStatus.Suspended;
            return await db.SaveChangesAsync();
        });
        var response = await Send(HttpMethod.Get, path, null, actor);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    private async Task<JsonElement> Ok(HttpMethod method, string path, object? body = null, bool management = true)
    {
        var result = await Send(method, path, body, management ? fixture.Admin : null);
        Assert.True(result.Status == HttpStatusCode.OK, $"{method} {path}: {(int)result.Status} {result.Body}");
        return result.Body;
    }
    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, object? body = null, TestUser? actor = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Moore-App-Environment", "local");
        if (actor is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        if (body is not null) request.Content = JsonContent.Create(body, options: WireJson);
        using var response = await fixture.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return (response.StatusCode, default);
        using var json = JsonDocument.Parse(text);
        return (response.StatusCode, json.RootElement.Clone());
    }
}
