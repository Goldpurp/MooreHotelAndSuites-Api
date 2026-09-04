using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.DTOs;
using Microsoft.EntityFrameworkCore;
using MooreHotels.WebAPI.Services;
using Microsoft.AspNetCore.RateLimiting;
using MooreHotels.WebAPI.Extensions;
using MooreHotels.Domain.Entities;
using System.Security.Claims;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/images")]
[Authorize]
public class ImagesController : ControllerBase
{
    private readonly IImageService _imageService;
    private readonly MooreHotels.Infrastructure.Persistence.MooreHotelsDbContext _context;
    private readonly ILogger<ImagesController> _logger;

    public ImagesController(
        IImageService imageService,
        MooreHotels.Infrastructure.Persistence.MooreHotelsDbContext context,
        ILogger<ImagesController> logger)
    {
        _imageService = imageService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a single image to a specified folder.
    /// Default folder is 'website-assets' for general UI components.
    /// </summary>
    [HttpPost("upload")]
    [Authorize(Roles = "Admin,Manager")]
    [EnableRateLimiting(ServiceCollectionExtensions.ImageUploadRateLimitPolicy)]
    [RequestSizeLimit(ImageFileValidator.MaxFileBytes + 64 * 1024)]
    [ProducesResponseType(typeof(ImageUploadResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(IFormFile file, [FromQuery] string folder = "website-assets")
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "File is empty or not provided." });

        var validationError = await ImageFileValidator.GetValidationErrorAsync(file);
        if (validationError is not null) return BadRequest(new { message = validationError });

        var allowedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "website-assets", "general" };
        if (!allowedFolders.Contains(folder))
        {
            return BadRequest(new
            {
                message = "Use the dedicated room or profile endpoint for room and avatar images."
            });
        }

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uploaderId))
        {
            return Unauthorized();
        }

        ImageUploadResult? result = null;
        try
        {
            result = await _imageService.UploadImageAsync(file, folder);

            if (result == null)
                return BadRequest(new { message = "Upload failed at the cloud provider." });

            _context.MediaAssets.Add(new MediaAsset
            {
                Id = Guid.NewGuid(),
                Url = result.Url,
                PublicId = result.PublicId,
                Folder = folder.ToLowerInvariant(),
                UploadedByUserId = uploaderId,
                CreatedAtUtc = DateTime.UtcNow
            });
            await _context.SaveChangesAsync(HttpContext.RequestAborted);

            return Ok(result);
        }
        catch (Exception exception)
        {
            if (result is not null)
            {
                try
                {
                    await _imageService.DeleteImageAsync(result.PublicId);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(
                        cleanupException,
                        "Could not remove untracked image {PublicId} after persistence failure.",
                        result.PublicId);
                }
            }
            _logger.LogError(exception, "Image upload failed for folder {Folder}.", folder);
            return StatusCode(500, new { message = "The image could not be uploaded." });
        }
    }

    /// <summary>
    /// Deletes an image from Cloudinary using its PublicId.
    /// </summary>
    [HttpDelete("delete")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Delete([FromQuery] string publicId)
    {
        if (string.IsNullOrWhiteSpace(publicId) || publicId.Length > 512)
            return BadRequest(new { message = "PublicId is required." });

        if (await _context.Users.AsNoTracking().AnyAsync(user => user.AvatarPublicId == publicId))
        {
            return Conflict(new
            {
                message = "This image is an active profile photo and must be replaced through the profile avatar endpoint."
            });
        }

        var dbImage = await _context.RoomImages.FirstOrDefaultAsync(image => image.PublicId == publicId);
        var mediaAsset = await _context.MediaAssets.FirstOrDefaultAsync(asset => asset.PublicId == publicId);
        if (dbImage is null && mediaAsset is null)
        {
            return NotFound(new { message = "Image is not registered to this application." });
        }

        if (dbImage is not null)
        {
            // Remove application references first. A provider outage may leave an
            // orphaned asset, but it must never leave the application pointing at
            // an image that has already been destroyed.
            _context.RoomImages.Remove(dbImage);
        }
        if (mediaAsset is not null) _context.MediaAssets.Remove(mediaAsset);
        await _context.SaveChangesAsync();

        bool providerDeleted;
        try
        {
            providerDeleted = await _imageService.DeleteImageAsync(publicId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Image {PublicId} was detached but provider cleanup failed.", publicId);
            providerDeleted = false;
        }

        return Ok(new
        {
            message = providerDeleted
                ? "Image removed successfully."
                : "Image removed from the application; storage cleanup will need to be retried.",
            storageDeleted = providerDeleted
        });
    }


}
