using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/admin/management")]
public class StaffController : ControllerBase
{
    private readonly IStaffService _staffService;
    private readonly MooreHotelsDbContext _context;

    public StaffController(IStaffService staffService, MooreHotelsDbContext context)
    {
        _staffService = staffService;
        _context = context;
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
        return Ok(await _staffService.GetAllStaffAsync());
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
    public async Task<IActionResult> Onboard([FromBody] OnboardUserRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();
        await _staffService.OnboardUserAsync(request, actingUserId);
        return Ok(new { Message = "Staff member provisioned. A secure setup link has been emailed." });
    }

    [HttpPut("employees/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateStaffRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync();
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM users WHERE \"Id\" = {id} FOR UPDATE");
            await _staffService.UpdateUserAsync(id, request, actingUserId);
            await transaction.CommitAsync();
        });
        return Ok(new { Message = "Staff profile updated. Existing sessions have been revoked." });
    }


    [HttpPatch("accounts/{id}/status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        [FromBody] ChangeStatusRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();
        await _staffService.ChangeUserStatusAsync(id, request.Status, actingUserId);
        return Ok(new { Message = "Account status updated successfully." });
    }


    [HttpPost("accounts/{id}/deactivate")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        return await ChangeStatus(id, new ChangeStatusRequest(ProfileStatus.Suspended));
    }


    [HttpPost("accounts/{id}/activate")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Activate(Guid id)
    {
        return await ChangeStatus(id, new ChangeStatusRequest(ProfileStatus.Active));
    }



    [HttpDelete("accounts/{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var actingUserId)) return Unauthorized();
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync();
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM users WHERE \"Id\" = {id} FOR UPDATE");
            await _staffService.DeleteUserAsync(id, actingUserId);
            await transaction.CommitAsync();
        });
        return NoContent();
    }
}
