using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MooreHotels.WebAPI.Services;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using MooreHotels.WebAPI.Extensions;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/rooms")]
public class RoomsController : ControllerBase
{
    private readonly IRoomService _roomService;
    private readonly IImageService _imageService;
    private readonly MooreHotelsDbContext _context;
    private readonly IMediaDeletionOutbox _mediaDeletionOutbox;
    private readonly OrphanedMediaCleanup _orphanedMediaCleanup;

    public RoomsController(
        IRoomService roomService,
        IImageService imageService,
        MooreHotelsDbContext context,
        IMediaDeletionOutbox mediaDeletionOutbox,
        OrphanedMediaCleanup orphanedMediaCleanup)
    {
        _roomService = roomService;
        _imageService = imageService;
        _context = context;
        _mediaDeletionOutbox = mediaDeletionOutbox;
        _orphanedMediaCleanup = orphanedMediaCleanup;
    }

    [HttpGet]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public async Task<IActionResult> GetRooms([FromQuery] RoomCategory? category)
    {
        var includeOffline = User.IsInRole("Admin") || User.IsInRole("Manager") || User.IsInRole("Staff");
        var rooms = await _roomService.GetAllRoomsAsync(category, includeOffline);
        return Ok(includeOffline ? rooms : rooms.Select(ToPublicRoom));
    }

    [HttpGet("search")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public async Task<ActionResult<IEnumerable<RoomDto>>> SearchRooms(
        [FromQuery] DateTime? checkIn,
        [FromQuery] DateTime? checkOut,
        [FromQuery] RoomCategory? category,
        [FromQuery] int guest = 1,
        [FromQuery] string? roomNumber = null,
        [FromQuery] string? amenity = null)
    {
        var canViewInternalFields = User.IsInRole("Admin") || User.IsInRole("Manager") || User.IsInRole("Staff");
        var request = new RoomSearchRequest(
            checkIn,
            checkOut,
            category,
            guest,
            canViewInternalFields ? roomNumber : null,
            amenity);
        var result = await _roomService.SearchRoomsAsync(request);
        return Ok(canViewInternalFields ? result : result.Select(ToPublicRoom));
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public async Task<IActionResult> GetRoom(Guid id)
    {
        var room = await _roomService.GetRoomByIdAsync(id);
        var canViewOffline = User.IsInRole("Admin") || User.IsInRole("Manager") || User.IsInRole("Staff");
        return room == null || (!room.IsOnline && !canViewOffline)
            ? NotFound(new { message = "Room not found." })
            : Ok(canViewOffline ? room : ToPublicRoom(room));
    }

    [HttpGet("{id:guid}/availability")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public async Task<IActionResult> GetAvailability(Guid id, [FromQuery] DateTime checkIn, [FromQuery] DateTime checkOut)
    {
        if (checkIn >= checkOut) return BadRequest("Check-out must be after check-in.");
        var exists = await _roomService.GetRoomByIdAsync(id);
        if (exists == null) return NotFound(new { message = "Room not found." });

        var result = await _roomService.CheckAvailabilityAsync(id, checkIn, checkOut);
        return Ok(result);
    }


    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(ServiceCollectionExtensions.ImageUploadRateLimitPolicy)]
    [RequestSizeLimit(ImageFileValidator.MaxMultipartRequestBytes)]
    public async Task<IActionResult> CreateRoom([FromForm] CreateRoomRequest request, List<IFormFile> files)
    {
        var validationError = await ImageFileValidator.GetValidationErrorAsync(files);
        if (validationError is not null) return BadRequest(new { message = validationError });

        var uploadResults = await _imageService.UploadMultipleAsync(files ?? [], "rooms");
        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync();
                var roomDto = await _roomService.CreateRoomAsync(request);
                foreach (var result in uploadResults)
                {
                    _context.RoomImages.Add(new RoomImage
                    {
                        Id = Guid.NewGuid(),
                        RoomId = roomDto.Id,
                        Url = result.Url,
                        PublicId = result.PublicId,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                var imageUrls = uploadResults.Select(u => u.Url).ToList();
                var finalResponse = roomDto with { Images = imageUrls };
                return CreatedAtAction(nameof(GetRoom), new { id = roomDto.Id }, finalResponse);
            });
        }
        catch
        {
            foreach (var result in uploadResults)
            {
                await _orphanedMediaCleanup.DeleteNowOrEnqueueAsync(
                    result.PublicId,
                    "OrphanedRoomCreate",
                    HttpContext.TraceIdentifier);
            }

            throw;
        }
    }




    [HttpPost("{id:guid}/images")]
    [Authorize(Roles = "Admin,Manager")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(ServiceCollectionExtensions.ImageUploadRateLimitPolicy)]
    [RequestSizeLimit(ImageFileValidator.MaxMultipartRequestBytes)]
    public async Task<IActionResult> AddImages(Guid id, List<IFormFile> files)
    {
        if (files == null || files.Count == 0) return BadRequest("No files uploaded.");
        var validationError = await ImageFileValidator.GetValidationErrorAsync(files);
        if (validationError is not null) return BadRequest(new { message = validationError });

        if (await _roomService.GetRoomByIdAsync(id) is null)
        {
            return NotFound(new { message = "Room not found." });
        }

        // External uploads are deliberately outside EF's retry delegate so a
        // transient database retry cannot upload the same image twice.
        var uploadResults = await _imageService.UploadMultipleAsync(files, "rooms");

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync();

                var lockedRoom = await _context.Rooms
                    .FromSqlInterpolated(
                        $"SELECT * FROM rooms WHERE \"Id\" = {id} FOR UPDATE")
                    .SingleOrDefaultAsync();
                if (lockedRoom is null)
                {
                    throw new NotFoundException("Room not found.");
                }

                var currentImageCount = await _context.RoomImages
                    .CountAsync(image => image.RoomId == id);
                if (currentImageCount + uploadResults.Count > 30)
                {
                    throw new BadRequestException(
                        "A room gallery cannot contain more than 30 images.");
                }

                foreach (var result in uploadResults)
                {
                    _context.RoomImages.Add(new RoomImage
                    {
                        Id = Guid.NewGuid(),
                        RoomId = id,
                        Url = result.Url,
                        PublicId = result.PublicId,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { message = "Images added successfully." });
            });
        }
        catch
        {
            foreach (var result in uploadResults)
            {
                await _orphanedMediaCleanup.DeleteNowOrEnqueueAsync(
                    result.PublicId,
                    "OrphanedRoomImage",
                    id.ToString());
            }

            throw;
        }
    }



    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(ServiceCollectionExtensions.ImageUploadRateLimitPolicy)]
    [RequestSizeLimit(ImageFileValidator.MaxMultipartRequestBytes)]
    [ProducesResponseType(typeof(RoomDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRoom(Guid id, [FromForm] UpdateRoomRequest request, List<IFormFile> files)
    {
        var validationError = await ImageFileValidator.GetValidationErrorAsync(files);
        if (validationError is not null) return BadRequest(new { message = validationError });

        var currentRoom = await _roomService.GetRoomByIdAsync(id);
        if (currentRoom is null)
        {
            return NotFound(new { message = "Room not found." });
        }

        var retainedImageUrls = request.Images?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var unknownImageUrl = retainedImageUrls.FirstOrDefault(value =>
            !currentRoom.Images.Contains(value, StringComparer.Ordinal));
        if (unknownImageUrl is not null)
        {
            return BadRequest(new
            {
                message = "Images may only contain URLs already assigned to this room. Upload new images as files."
            });
        }

        var willReplaceImages = request.ReplaceImages == true || request.Images is not null;
        var retainedCount = willReplaceImages ? retainedImageUrls.Count : currentRoom.Images.Count;
        if (retainedCount + (files?.Count ?? 0) > 30)
        {
            return BadRequest(new { message = "A room gallery cannot contain more than 30 images." });
        }

        var uploadResults = files is { Count: > 0 }
            ? await _imageService.UploadMultipleAsync(files, "rooms")
            : [];
        var publicIdsToDelete = new List<string>();
        _context.ChangeTracker.Clear();

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                publicIdsToDelete.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync();

                var lockedRoom = await _context.Rooms
                    .FromSqlInterpolated(
                        $"SELECT * FROM rooms WHERE \"Id\" = {id} FOR UPDATE")
                    .SingleOrDefaultAsync();
                if (lockedRoom is null)
                {
                    throw new NotFoundException("Room not found.");
                }

                var currentImageCount = await _context.RoomImages
                    .CountAsync(image => image.RoomId == id);
                var committedRetainedCount = willReplaceImages
                    ? retainedImageUrls.Count
                    : currentImageCount;
                if (committedRetainedCount + uploadResults.Count > 30)
                {
                    throw new BadRequestException(
                        "A room gallery cannot contain more than 30 images.");
                }

                await _roomService.UpdateRoomAsync(id, request);

                // ReplaceImages disambiguates "replace everything with new uploads"
                // from "the client did not send image changes".
                if (willReplaceImages)
                {
                    var existingImages = await _context.RoomImages
                        .Where(img => img.RoomId == id)
                        .ToListAsync();
                    var missingRetainedImage = retainedImageUrls.FirstOrDefault(value =>
                        existingImages.All(image =>
                            !string.Equals(image.Url, value, StringComparison.Ordinal)));
                    if (missingRetainedImage is not null)
                    {
                        throw new BadRequestException(
                            "The room gallery changed while this edit was being saved. Refresh and try again.");
                    }

                    var imagesToDelete = existingImages
                        .Where(img => !retainedImageUrls.Contains(img.Url))
                        .ToList();

                    if (imagesToDelete.Count > 0)
                    {
                        _context.RoomImages.RemoveRange(imagesToDelete);
                        publicIdsToDelete.AddRange(imagesToDelete
                            .Select(image => image.PublicId)
                            .Where(publicId => !string.IsNullOrWhiteSpace(publicId)));
                    }
                }

                foreach (var result in uploadResults)
                {
                    _context.RoomImages.Add(new RoomImage
                    {
                        Id = Guid.NewGuid(),
                        RoomId = id,
                        Url = result.Url,
                        PublicId = result.PublicId,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                foreach (var publicId in publicIdsToDelete.Distinct(StringComparer.Ordinal))
                {
                    _context.MediaDeletionJobs.Add(_mediaDeletionOutbox.Create(
                        publicId,
                        "Room",
                        id.ToString()));
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            });
        }
        catch
        {
            foreach (var result in uploadResults)
            {
                await _orphanedMediaCleanup.DeleteNowOrEnqueueAsync(
                    result.PublicId,
                    "OrphanedRoomUpdate",
                    id.ToString());
            }

            throw;
        }

        _context.ChangeTracker.Clear();
        var updatedRoom = await _roomService.GetRoomByIdAsync(id);
        return Ok(updatedRoom);
    }



    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> DeleteRoom(Guid id)
    {
        if (await _roomService.GetRoomByIdAsync(id) is null)
        {
            return NotFound(new { message = "Room not found." });
        }
        if (await _context.Bookings.AsNoTracking().AnyAsync(booking => booking.RoomId == id))
        {
            return Conflict(new
            {
                message = "A room with booking history cannot be deleted. Mark it as maintenance or offline instead."
            });
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var publicIds = await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync();
            var deletedPublicIds = await _roomService.DeleteRoomAsync(id);
            foreach (var publicId in deletedPublicIds
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.Ordinal))
            {
                _context.MediaDeletionJobs.Add(_mediaDeletionOutbox.Create(
                    publicId,
                    "Room",
                    id.ToString()));
            }
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return deletedPublicIds;
        });

        return Accepted(new
        {
            message = "Room deleted and durable image cleanup queued.",
            queuedImageCount = publicIds.Count
        });
    }

    private static PublicRoomDto ToPublicRoom(RoomDto room) => new(
        room.Id,
        room.Name,
        room.Category,
        room.PricePerNight,
        room.Capacity,
        room.Size,
        room.Description,
        room.Amenities,
        room.Images);




}
