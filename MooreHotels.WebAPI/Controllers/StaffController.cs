using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;
using MooreHotels.WebAPI.Extensions;
using System.Security.Claims;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/admin/management")]
public class StaffController : ControllerBase
{
    private readonly IStaffService _staffService;

    public StaffController(IStaffService staffService)
    {
        _staffService = staffService;
    }

    [HttpGet("stats")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetStats()
    {
        return Ok(await _staffService.GetStaffStatsAsync());
    }

    [HttpGet("employees")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetStaffList()
    {
        var staff = await _staffService.GetAllStaffAsync();
        return Ok(User.IsInRole("Admin")
            ? staff
            : staff.Where(item => item.Role != UserRole.Admin));
    }

    [HttpGet("clients")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetGuestUserList()
    {
        var allUsers = await _staffService.GetAllUsersAsync();
        return Ok(allUsers.Where(u => u.Role == UserRole.Client));
    }

    [HttpPost("onboard-staff")]
    [Authorize(Roles = "Admin,Manager")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> Onboard([FromBody] OnboardUserRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();
        await _staffService.OnboardUserAsync(request, actingUserId);
        return Ok(new { Message = "Staff member provisioned. A secure setup link has been emailed." });
    }

    [HttpPut("employees/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateStaffRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();

        await _staffService.UpdateUserAsync(id, request, actingUserId);
        return Ok(new { Message = "Staff profile updated. Existing sessions have been revoked." });
    }


    [HttpPatch("accounts/{id}/status")]
    [Authorize(Roles = "Admin")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        [FromBody] ChangeStatusRequest request)
    {
        return await ChangeStatusCore(id, request.Status);
    }


    [HttpPost("accounts/{id}/deactivate")]
    [Authorize(Roles = "Admin,Manager")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        return await ChangeStatusCore(id, ProfileStatus.Suspended);
    }


    [HttpPost("accounts/{id}/activate")]
    [Authorize(Roles = "Admin,Manager")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> Activate(Guid id)
    {
        return await ChangeStatusCore(id, ProfileStatus.Active);
    }

    [HttpPost("accounts/{id:guid}/emergency-suspend-admin")]
    [Authorize(Roles = "Admin")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> EmergencySuspendAdministrator(
        Guid id,
        [FromBody] EmergencyAdminStatusRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();
        await _staffService.ChangeAdministratorStatusAsync(
            id,
            ProfileStatus.Suspended,
            request,
            actingUserId);
        return Ok(new
        {
            Message = "Administrator suspended. Existing sessions have been revoked."
        });
    }

    [HttpPost("accounts/{id:guid}/emergency-reactivate-admin")]
    [Authorize(Roles = "Admin")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> EmergencyReactivateAdministrator(
        Guid id,
        [FromBody] EmergencyAdminStatusRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();
        await _staffService.ChangeAdministratorStatusAsync(
            id,
            ProfileStatus.Active,
            request,
            actingUserId);
        return Ok(new
        {
            Message = "Administrator reactivated. They must sign in again."
        });
    }



    [HttpDelete("accounts/{id}")]
    [Authorize(Roles = "Admin")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();
        await _staffService.DeleteUserAsync(id, actingUserId);
        return Ok(new
        {
            Message = "Staff account decommissioned, personal profile data removed, and sessions revoked."
        });
    }

    private async Task<IActionResult> ChangeStatusCore(Guid id, ProfileStatus status)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();
        await _staffService.ChangeUserStatusAsync(id, status, actingUserId);
        return Ok(new { Message = "Account status updated successfully." });
    }
}
