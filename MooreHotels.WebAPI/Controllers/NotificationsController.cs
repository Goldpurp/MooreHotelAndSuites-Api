using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Common;
using System.Security.Claims;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly IAuthorizationService _authorizationService;

    public NotificationsController(
        INotificationService notificationService,
        IAuthorizationService authorizationService)
    {
        _notificationService = notificationService;
        _authorizationService = authorizationService;
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
