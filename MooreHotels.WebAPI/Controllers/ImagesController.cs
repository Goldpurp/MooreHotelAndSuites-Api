using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.DTOs;
using Microsoft.EntityFrameworkCore;
using MooreHotels.WebAPI.Services;

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
            { "website-assets", "rooms", "avatars", "general" };
        if (!allowedFolders.Contains(folder)) folder = "general";
        if (!folder.Equals("avatars", StringComparison.OrdinalIgnoreCase) &&
            !User.IsInRole("Admin") && !User.IsInRole("Manager"))
        {
            return Forbid();
        }

        try
        {
            var result = await _imageService.UploadImageAsync(file, folder);

            if (result == null)
                return BadRequest(new { message = "Upload failed at the cloud provider." });

            return Ok(result);
        }
        catch (Exception exception)
        {
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
        if (string.IsNullOrWhiteSpace(publicId))
            return BadRequest(new { message = "PublicId is required." });

        if (await _context.Users.AsNoTracking().AnyAsync(user => user.AvatarPublicId == publicId))
        {
            return Conflict(new
            {
                message = "This image is an active profile photo and must be replaced through the profile avatar endpoint."
            });
        }

        var dbImage = await _context.RoomImages.FirstOrDefaultAsync(image => image.PublicId == publicId);
        if (dbImage is not null)
        {
            // Remove application references first. A provider outage may leave an
            // orphaned asset, but it must never leave the application pointing at
            // an image that has already been destroyed.
            _context.RoomImages.Remove(dbImage);
            await _context.SaveChangesAsync();
        }

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

        if (!providerDeleted && dbImage is null)
            return NotFound(new { message = "Image not found in storage or the application database." });

        return Ok(new
        {
            message = providerDeleted
                ? "Image removed successfully."
                : "Image removed from the application; storage cleanup will need to be retried.",
            storageDeleted = providerDeleted
        });
    }


}
