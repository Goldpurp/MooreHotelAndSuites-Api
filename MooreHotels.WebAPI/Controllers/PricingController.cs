using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.WebAPI.Extensions;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/pricing")]
public sealed class PricingController : ControllerBase
{
    private readonly IPricingService _pricing;

    public PricingController(IPricingService pricing) => _pricing = pricing;

    [HttpPost("quotes")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
    public async Task<ActionResult<PricingQuoteDto>> CreateQuote(
        [FromBody] CreatePricingQuoteRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.CreateQuoteAsync(request, cancellationToken));

    [HttpGet("configuration")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<PricingConfigurationDto>> GetConfiguration(
        CancellationToken cancellationToken) =>
        Ok(await _pricing.GetConfigurationAsync(cancellationToken));

    [HttpPost("rate-plans")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> CreateRatePlan(
        [FromBody] RatePlanRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.SaveRatePlanAsync(
            null, request, GetActorId(), cancellationToken));

    [HttpPut("rate-plans/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateRatePlan(
        Guid id,
        [FromBody] RatePlanRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.SaveRatePlanAsync(
            id, request, GetActorId(), cancellationToken));

    [HttpPost("daily-rates")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> CreateDailyRate(
        [FromBody] DailyRoomRateRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.SaveDailyRateAsync(
            null, request, GetActorId(), cancellationToken));

    [HttpPut("daily-rates/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateDailyRate(
        Guid id,
        [FromBody] DailyRoomRateRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.SaveDailyRateAsync(
            id, request, GetActorId(), cancellationToken));

    [HttpDelete("daily-rates/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> DeleteDailyRate(
        Guid id,
        CancellationToken cancellationToken)
    {
        await _pricing.DeleteDailyRateAsync(id, GetActorId(), cancellationToken);
        return NoContent();
    }

    [HttpPost("rules")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> CreateRule(
        [FromBody] PricingRuleRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.SaveRuleAsync(
            null, request, GetActorId(), cancellationToken));

    [HttpPut("rules/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateRule(
        Guid id,
        [FromBody] PricingRuleRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.SaveRuleAsync(
            id, request, GetActorId(), cancellationToken));

    [HttpPost("promotions")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> CreatePromotion(
        [FromBody] PromotionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.SavePromotionAsync(
            null, request, GetActorId(), cancellationToken));

    [HttpPut("promotions/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdatePromotion(
        Guid id,
        [FromBody] PromotionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.SavePromotionAsync(
            id, request, GetActorId(), cancellationToken));

    private Guid GetActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)
            ? actorId
            : throw new UnauthorizedAccessException("The authenticated actor is invalid.");
}
