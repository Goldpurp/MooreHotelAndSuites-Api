using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Interfaces.Services;
using System.Security.Claims;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet("staff")]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> GetStaffNotifications()
    {
        return Ok(await _notificationService.GetStaffNotificationsAsync());
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

        var canManageStaffNotifications =
            User.IsInRole("Admin") || User.IsInRole("Manager") || User.IsInRole("Staff");
        await _notificationService.MarkAsReadAsync(id, userId, canManageStaffNotifications);
        return NoContent();
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }
}
