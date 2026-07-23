using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Services;
using System.Security.Claims;
using System.Text.Json;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly IProfileService _profileService;
    private readonly IImageService _imageService;
    private readonly MooreHotelsDbContext _context;
    private readonly ILogger<ProfileController> _logger;

    public ProfileController(
        IProfileService profileService,
        IImageService imageService,
        MooreHotelsDbContext context,
        ILogger<ProfileController> logger)
    {
        _profileService = profileService;
        _imageService = imageService;
        _context = context;
        _logger = logger;
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMyProfile()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty) return Unauthorized(new { Message = "Authorization Protocol Error: User ID not found in security context." });
        
        return Ok(await _profileService.GetProfileAsync(userId));
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty) return Unauthorized();

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync();
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM users WHERE \"Id\" = {userId} FOR UPDATE");
            await _profileService.UpdateProfileAsync(userId, request);
            await transaction.CommitAsync();
        });

        _context.ChangeTracker.Clear();
        var profile = await _profileService.GetProfileAsync(userId);
        return Ok(new { Message = "User profile updated successfully.", Data = profile });
    }

    /// <summary>
    /// Replaces the signed-in user's avatar as one managed operation. The new
    /// asset is removed if persistence fails, and the previous asset is only
    /// removed after the database commit succeeds.
    /// </summary>
    [HttpPut("me/avatar")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImageFileValidator.MaxFileBytes + 64 * 1024)]
    [ProducesResponseType(typeof(AvatarUpdateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateMyAvatar(IFormFile file)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty) return Unauthorized();
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "Choose an image to upload." });
        }

        var validationError = await ImageFileValidator.GetValidationErrorAsync(file);
        if (validationError is not null) return BadRequest(new { message = validationError });

        var uploaded = await _imageService.UploadImageAsync(file, "avatars");
        if (uploaded is null)
        {
            return BadRequest(new { message = "The avatar upload did not produce an image." });
        }

        string? previousPublicId = null;
        var updatedAtUtc = DateTime.UtcNow;
        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync();

                // Serialize concurrent avatar changes so an older request cannot
                // delete the asset selected by a newer request.
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM users WHERE \"Id\" = {userId} FOR UPDATE");
                var user = await _context.Users.SingleOrDefaultAsync(item => item.Id == userId);
                if (user is null)
                {
                    throw new InvalidOperationException("The authenticated user no longer exists.");
                }

                previousPublicId = user.AvatarPublicId;
                var hadPreviousAvatar = !string.IsNullOrWhiteSpace(user.AvatarUrl);
                user.AvatarUrl = uploaded.Url;
                user.AvatarPublicId = uploaded.PublicId;

                if (!string.IsNullOrWhiteSpace(user.GuestId))
                {
                    var guest = await _context.Guests.SingleOrDefaultAsync(item => item.Id == user.GuestId);
                    if (guest is not null) guest.AvatarUrl = uploaded.Url;
                }

                _context.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ProfileId = userId,
                    Action = "PROFILE_AVATAR_UPDATED",
                    EntityType = "User",
                    EntityId = userId.ToString(),
                    OldDataJson = JsonSerializer.Serialize(new { HadAvatar = hadPreviousAvatar }),
                    NewDataJson = JsonSerializer.Serialize(new
                    {
                        HasAvatar = true,
                        UpdatedAtUtc = updatedAtUtc,
                        RequestId = HttpContext.TraceIdentifier
                    }),
                    CreatedAt = updatedAtUtc
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            });
        }
        catch
        {
            try
            {
                await _imageService.DeleteImageAsync(uploaded.PublicId);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(
                    cleanupException,
                    "Could not remove new avatar {PublicId} after persistence failure.",
                    uploaded.PublicId);
            }

            throw;
        }

        if (!string.IsNullOrWhiteSpace(previousPublicId) &&
            !string.Equals(previousPublicId, uploaded.PublicId, StringComparison.Ordinal))
        {
            try
            {
                await _imageService.DeleteImageAsync(previousPublicId);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(
                    cleanupException,
                    "Avatar changed, but the superseded asset {PublicId} could not be removed.",
                    previousPublicId);
            }
        }

        return Ok(new AvatarUpdateResponse(
            "Profile photo updated successfully.",
            new AvatarUpdateData(uploaded.Url, updatedAtUtc)));
    }

    [HttpGet("bookings")]
    public async Task<IActionResult> GetMyBookings()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty) return Unauthorized();
        return Ok(await _profileService.GetBookingHistoryAsync(userId));
    }

    [HttpPost("rotate-security")]
    public async Task<IActionResult> RotateCredentials([FromBody] RotateCredentialsRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty) return Unauthorized();

        await _profileService.RotateCredentialsAsync(userId, request);
        return Ok(new { Message = "Password updated successfully. Sign in again with the new password." });
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }
}
