using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MooreHotels.Application.DTOs;
using MooreHotels.Infrastructure.Services;
using MooreHotels.WebAPI.Extensions;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/bookings")]
public sealed class PaymentReviewsController(PaymentReviewService reviews) : ControllerBase
{
    [HttpPost("{code}/report-transfer")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
    public async Task<IActionResult> Report(string code, ReportTransferRequest request, CancellationToken ct)
    {
        Guid? userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
        return Ok(await reviews.ReportAsync(code, request.GuestAccessToken, userId, ct));
    }

    [HttpGet("payment-reviews")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return Ok(await reviews.GetQueueAsync(userId, ct));
    }

    [HttpGet("{code}/review-transfer/rooms")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ReplacementRooms(string code, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return Ok(await reviews.GetReplacementRoomsAsync(code, userId, ct));
    }

    [HttpPost("{code}/review-transfer")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Resolve(string code, ResolveTransferRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return Ok(await reviews.ResolveAsync(code, request, userId, ct));
    }
}
