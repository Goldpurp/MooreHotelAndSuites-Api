using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Common;
using MooreHotels.WebAPI.Extensions;
using MooreHotels.WebAPI.Services;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly IAuthorizationService _authorizationService;
    private readonly RealtimeAccessTicketStore _realtimeTickets;

    public NotificationsController(
        INotificationService notificationService,
        IAuthorizationService authorizationService,
        RealtimeAccessTicketStore realtimeTickets)
    {
        _notificationService = notificationService;
        _authorizationService = authorizationService;
        _realtimeTickets = realtimeTickets;
    }

    [HttpPost("realtime-ticket")]
    [Authorize(Policy = HotelAuthorization.ReservationsRead)]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public IActionResult IssueRealtimeTicket()
    {
        var authorization = Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Unauthorized();

        var bearerToken = authorization[bearerPrefix.Length..].Trim();
        if (bearerToken.Length is 0 or > 8192)
            return Unauthorized();

        var ticket = _realtimeTickets.Issue(bearerToken);
        return Ok(new
        {
            ticket.Ticket,
            ticket.ExpiresAtUtc,
            Transport = "WebSockets",
            SkipNegotiation = true
        });
    }

    [HttpGet("staff")]
    [Authorize(Policy = HotelAuthorization.ReservationsRead)]
    public async Task<IActionResult> GetStaffNotifications()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty) return Unauthorized();
        return Ok(await _notificationService.GetStaffNotificationsAsync(userId));
    }

    [HttpGet("my")]
    public async Task<IActionResult> GetMyNotifications()
    {
        var userId = GetUserId();
        return Ok(await _notificationService.GetUserNotificationsAsync(userId));
    }

    [HttpPatch("{id}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty) return Unauthorized();

        var canManageStaffNotifications = (await _authorizationService.AuthorizeAsync(
            User,
            HotelAuthorization.ReservationsRead)).Succeeded;
        await _notificationService.MarkAsReadAsync(id, userId, canManageStaffNotifications);
        return NoContent();
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }
}
