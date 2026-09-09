using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class ProductionReadinessSevenToTenTests
{
    private readonly ManualTransferTestFixture _fixture;

    public ProductionReadinessSevenToTenTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Quote_snapshots_daily_rates_discount_tax_and_fee_then_is_consumed_atomically()
    {
        var room = await _fixture.CreateRoomAsync();
        var ratePlanId = await _fixture.WithDbAsync(db => db.RatePlans
            .Where(plan => plan.IsDefault && plan.IsActive)
            .Select(plan => plan.Id)
            .SingleAsync());
        var checkIn = DateTime.UtcNow.Date.AddDays(20);
        var checkOut = checkIn.AddDays(2);

        await AssertOkAsync(AuthorizedJson(
            HttpMethod.Post,
            "/api/pricing/daily-rates",
            new
            {
                ratePlanId,
                roomCategory = "standard",
                stayDate = DateOnly.FromDateTime(checkIn),
                amount = 90000m
            }));
        await AssertOkAsync(AuthorizedJson(
            HttpMethod.Post,
            "/api/pricing/rules",
            new
            {
                code = $"VAT-{Guid.NewGuid():N}"[..20],
                name = "VAT",
                kind = "tax",
                calculation = "percentage",
                value = 7.5m,
                currency = "NGN",
                isInclusive = false,
                sortOrder = 10,
                isActive = true
            }));
        await AssertOkAsync(AuthorizedJson(
            HttpMethod.Post,
            "/api/pricing/rules",
            new
            {
                code = $"SERVICE-{Guid.NewGuid():N}"[..24],
                name = "Service charge",
                kind = "fee",
                calculation = "fixedPerStay",
                value = 5000m,
                currency = "NGN",
                isInclusive = false,
                sortOrder = 20,
                isActive = true
            }));
        var promotionCode = $"SAVE-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        await AssertOkAsync(AuthorizedJson(
            HttpMethod.Post,
            "/api/pricing/promotions",
            new
            {
                code = promotionCode,
                name = "Ten percent test offer",
                discountType = "percentage",
                value = 10m,
                currency = "NGN",
                minimumNights = 2,
                ratePlanId,
                validFromUtc = DateTime.UtcNow.AddHours(-1),
                validUntilUtc = DateTime.UtcNow.AddDays(2),
                redemptionLimit = 1,
                isActive = true
            }));

        using var quoteRequest = PublicJson(
            "/api/pricing/quotes",
            new
            {
                roomId = room.Id,
                checkIn,
                checkOut,
                adultCount = 2,
                childCount = 0,
                ratePlanCode = "standard",
                promotionCode
            });
        using var quoteResponse = await _fixture.Client.SendAsync(quoteRequest);
        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);
        using var quoteJson = JsonDocument.Parse(await quoteResponse.Content.ReadAsStringAsync());
        var quote = quoteJson.RootElement;
        var quoteId = quote.GetProperty("quoteId").GetGuid();
        var quoteToken = quote.GetProperty("quoteToken").GetString()!;
        Assert.Equal("NGN", quote.GetProperty("currency").GetString());
        Assert.Equal(165000m, quote.GetProperty("roomSubtotal").GetDecimal());
        Assert.Equal(16500m, quote.GetProperty("discountAmount").GetDecimal());
        Assert.Equal(11137.50m, quote.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(5000m, quote.GetProperty("feeAmount").GetDecimal());
        Assert.Equal(164637.50m, quote.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(5, quote.GetProperty("lines").GetArrayLength());

        using var bookingRequest = PublicJson(
            "/api/bookings",
            new
            {
                roomId = room.Id,
                guestFirstName = "Quoted",
                guestLastName = "Guest",
                guestEmail = $"quote-{Guid.NewGuid():N}@example.test",
                guestPhone = "+2348000000044",
                checkIn,
                checkOut,
                adultCount = 2,
                childCount = 0,
                paymentMethod = "directTransfer",
                quoteId,
                quoteToken
            });
        using var bookingResponse = await _fixture.Client.SendAsync(bookingRequest);
        Assert.Equal(HttpStatusCode.OK, bookingResponse.StatusCode);
        using var bookingJson = JsonDocument.Parse(await bookingResponse.Content.ReadAsStringAsync());
        Assert.Equal(164637.50m, bookingJson.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(quoteId, bookingJson.RootElement.GetProperty("quoteId").GetGuid());

        var stored = await _fixture.WithDbAsync(async db => new
        {
            Quote = await db.BookingQuotes.AsNoTracking()
                .Include(item => item.Lines)
                .SingleAsync(item => item.Id == quoteId),
            Booking = await db.Bookings.AsNoTracking()
                .SingleAsync(item => item.QuoteId == quoteId),
            PromotionRedemptions = await db.Promotions
                .Where(item => item.Code == promotionCode)
                .Select(item => item.RedemptionCount)
                .SingleAsync(),
            AuditCount = await db.AuditLogs.CountAsync(log =>
                log.EntityType == "PricingRule" ||
                log.EntityType == "Promotion" ||
                log.EntityType == "DailyRoomRate")
        });
        Assert.NotNull(stored.Quote.ConsumedAtUtc);
        Assert.Equal(5, stored.Quote.Lines.Count);
        Assert.Equal(stored.Quote.TotalAmount, stored.Booking.Amount);
        Assert.Equal(stored.Quote.RoomSubtotal, stored.Booking.RoomSubtotal);
        Assert.Equal(1, stored.PromotionRedemptions);
        Assert.True(stored.AuditCount >= 4);
    }

    [Fact]
    public async Task Expired_quote_and_changed_booking_details_are_rejected()
    {
        var room = await _fixture.CreateRoomAsync();
        var checkIn = DateTime.UtcNow.Date.AddDays(30);
        var checkOut = checkIn.AddDays(1);
        using var quoteRequest = PublicJson(
            "/api/pricing/quotes",
            new
            {
                roomId = room.Id,
                checkIn,
                checkOut,
                adultCount = 1,
                childCount = 0
            });
        using var quoteResponse = await _fixture.Client.SendAsync(quoteRequest);
        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);
        using var quoteJson = JsonDocument.Parse(await quoteResponse.Content.ReadAsStringAsync());
        var quoteId = quoteJson.RootElement.GetProperty("quoteId").GetGuid();
        var quoteToken = quoteJson.RootElement.GetProperty("quoteToken").GetString()!;

        using (var changedRequest = BookingRequest(
                   room.Id, checkIn, checkOut, quoteId, quoteToken, adultCount: 2))
        using (var changedResponse = await _fixture.Client.SendAsync(changedRequest))
        {
            Assert.Equal(HttpStatusCode.BadRequest, changedResponse.StatusCode);
            Assert.Contains(
                "changed after pricing",
                await changedResponse.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        await _fixture.WithDbAsync(async db =>
        {
            var quote = await db.BookingQuotes.SingleAsync(item => item.Id == quoteId);
            quote.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30);
            quote.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
            return true;
        });
        using var expiredRequest = BookingRequest(
            room.Id, checkIn, checkOut, quoteId, quoteToken, adultCount: 1);
        using var expiredResponse = await _fixture.Client.SendAsync(expiredRequest);
        Assert.Equal(HttpStatusCode.BadRequest, expiredResponse.StatusCode);
        Assert.Contains(
            "expired",
            await expiredResponse.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pricing_configuration_and_operational_evidence_are_admin_only()
    {
        using (var anonymous = await _fixture.Client.GetAsync("/api/pricing/configuration"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using (var staffRequest = Authorized(HttpMethod.Get, "/api/pricing/configuration", _fixture.Staff))
        using (var staffResponse = await _fixture.Client.SendAsync(staffRequest))
            Assert.Equal(HttpStatusCode.Forbidden, staffResponse.StatusCode);

        using (var managerRequest = Authorized(HttpMethod.Get, "/api/pricing/configuration", _fixture.Manager))
        using (var managerResponse = await _fixture.Client.SendAsync(managerRequest))
            Assert.Equal(HttpStatusCode.OK, managerResponse.StatusCode);

        using var healthRequest = Authorized(HttpMethod.Get, "/api/health", _fixture.Admin);
        using var healthResponse = await _fixture.Client.SendAsync(healthRequest);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        using var healthJson = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
        Assert.True(healthJson.RootElement.TryGetProperty("recovery", out _));
        Assert.True(healthJson.RootElement.TryGetProperty("providers", out _));
        Assert.True(healthJson.RootElement.GetProperty("emailQueue")
            .TryGetProperty("warningThresholdMinutes", out _));
        Assert.True(healthJson.RootElement.GetProperty("payments")
            .TryGetProperty("stalePending", out _));
    }

    [Fact]
    public async Task Pricing_preserves_a_default_plan_and_rejects_non_ngn_monnify_quotes()
    {
        var defaultPlan = await _fixture.WithDbAsync(db => db.RatePlans
            .AsNoTracking()
            .SingleAsync(plan => plan.IsDefault && plan.IsActive));
        using (var disableRequest = AuthorizedJson(
                   HttpMethod.Put,
                   $"/api/pricing/rate-plans/{defaultPlan.Id}",
                   new
                   {
                       defaultPlan.Code,
                       defaultPlan.Name,
                       defaultPlan.Description,
                       defaultPlan.Currency,
                       baseAdjustmentType = "none",
                       defaultPlan.BaseAdjustmentValue,
                       defaultPlan.MinimumNights,
                       defaultPlan.MaximumNights,
                       defaultPlan.SellFromDate,
                       defaultPlan.SellUntilDate,
                       isDefault = false,
                       isActive = false
                   }))
        using (var disableResponse = await _fixture.Client.SendAsync(disableRequest))
        {
            Assert.Equal(HttpStatusCode.BadRequest, disableResponse.StatusCode);
            Assert.Contains(
                "another active default",
                await disableResponse.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        var usdPlanCode = $"USD-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        using var createPlanRequest = AuthorizedJson(
            HttpMethod.Post,
            "/api/pricing/rate-plans",
            new
            {
                code = usdPlanCode,
                name = "USD test plan",
                currency = "USD",
                baseAdjustmentType = "none",
                baseAdjustmentValue = 0m,
                minimumNights = 1,
                maximumNights = 90,
                isDefault = false,
                isActive = true
            });
        using var createPlanResponse = await _fixture.Client.SendAsync(createPlanRequest);
        Assert.Equal(HttpStatusCode.OK, createPlanResponse.StatusCode);

        var room = await _fixture.CreateRoomAsync();
        var checkIn = DateTime.UtcNow.Date.AddDays(40);
        var checkOut = checkIn.AddDays(1);
        using var quoteRequest = PublicJson(
            "/api/pricing/quotes",
            new
            {
                roomId = room.Id,
                checkIn,
                checkOut,
                adultCount = 1,
                childCount = 0,
                ratePlanCode = usdPlanCode
            });
        using var quoteResponse = await _fixture.Client.SendAsync(quoteRequest);
        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);
        using var quoteJson = JsonDocument.Parse(await quoteResponse.Content.ReadAsStringAsync());
        var quoteId = quoteJson.RootElement.GetProperty("quoteId").GetGuid();
        var quoteToken = quoteJson.RootElement.GetProperty("quoteToken").GetString()!;
        Assert.Equal("USD", quoteJson.RootElement.GetProperty("currency").GetString());

        using var bookingRequest = PublicJson(
            "/api/bookings",
            new
            {
                roomId = room.Id,
                guestFirstName = "Currency",
                guestLastName = "Guard",
                guestEmail = $"currency-guard-{Guid.NewGuid():N}@example.test",
                guestPhone = "+2348000000066",
                checkIn,
                checkOut,
                adultCount = 1,
                childCount = 0,
                paymentMethod = "monnify",
                quoteId,
                quoteToken
            });
        using var bookingResponse = await _fixture.Client.SendAsync(bookingRequest);
        Assert.Equal(HttpStatusCode.BadRequest, bookingResponse.StatusCode);
        Assert.Contains(
            "only for NGN",
            await bookingResponse.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    private HttpRequestMessage BookingRequest(
        Guid roomId,
        DateTime checkIn,
        DateTime checkOut,
        Guid quoteId,
        string quoteToken,
        int adultCount) => PublicJson(
        "/api/bookings",
        new
        {
            roomId,
            guestFirstName = "Quote",
            guestLastName = "Validation",
            guestEmail = $"quote-validation-{Guid.NewGuid():N}@example.test",
            guestPhone = "+2348000000055",
            checkIn,
            checkOut,
            adultCount,
            childCount = 0,
            paymentMethod = "directTransfer",
            quoteId,
            quoteToken
        });

    private async Task AssertOkAsync(HttpRequestMessage request)
    {
        using (request)
        using (var response = await _fixture.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private HttpRequestMessage AuthorizedJson(
        HttpMethod method,
        string path,
        object body)
    {
        var request = Authorized(method, path, _fixture.Manager);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Authorized(
        HttpMethod method,
        string path,
        TestUser actor)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private static HttpRequestMessage PublicJson(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }
}
