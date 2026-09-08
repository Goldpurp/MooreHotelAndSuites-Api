using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.WebAPI.Extensions;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/inventory")]
public sealed class InventoryController : ControllerBase
{
    private readonly IInventoryService _inventory;

    public InventoryController(IInventoryService inventory) => _inventory = inventory;

    [HttpGet("room-types")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public async Task<ActionResult<IReadOnlyList<PublicRoomTypeDto>>> GetPublicRoomTypes(
        CancellationToken cancellationToken)
    {
        var roomTypes = await _inventory.GetRoomTypesAsync(false, cancellationToken);
        return Ok(roomTypes.Select(ToPublicRoomType).ToArray());
    }

    [HttpGet("room-types/{roomTypeId:guid}/availability")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public async Task<ActionResult<PublicRoomTypeAvailabilityDto>> GetAvailability(
        Guid roomTypeId,
        [FromQuery] DateOnly checkIn,
        [FromQuery] DateOnly checkOut,
        [FromQuery] int units = 1,
        CancellationToken cancellationToken = default)
    {
        var result = await _inventory.GetAvailabilityAsync(
            roomTypeId, checkIn, checkOut, units, cancellationToken);
        return Ok(new PublicRoomTypeAvailabilityDto(
            result.RoomTypeId,
            result.RoomTypeCode,
            result.RoomTypeName,
            result.CheckInDate,
            result.CheckOutDate,
            result.RequestedUnits,
            result.Available));
    }

    [HttpGet("management/room-types")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<IReadOnlyList<RoomTypeDto>>> GetManagedRoomTypes(
        [FromQuery] bool includeInactive = true,
        CancellationToken cancellationToken = default) =>
        Ok(await _inventory.GetRoomTypesAsync(includeInactive, cancellationToken));

    [HttpGet("management/room-types/{roomTypeId:guid}/availability")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<RoomTypeAvailabilityDto>> GetManagedAvailability(
        Guid roomTypeId,
        [FromQuery] DateOnly checkIn,
        [FromQuery] DateOnly checkOut,
        [FromQuery] int units = 1,
        CancellationToken cancellationToken = default) =>
        Ok(await _inventory.GetAvailabilityAsync(
            roomTypeId, checkIn, checkOut, units, cancellationToken));

    [HttpPost("room-types")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<RoomTypeDto>> CreateRoomType(
        [FromBody] SaveRoomTypeRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _inventory.SaveRoomTypeAsync(
            null, request, GetActorId(), cancellationToken));

    [HttpPut("room-types/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<RoomTypeDto>> UpdateRoomType(
        Guid id,
        [FromBody] SaveRoomTypeRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _inventory.SaveRoomTypeAsync(
            id, request, GetActorId(), cancellationToken));

    [HttpGet("closures")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<IReadOnlyList<InventoryClosureDto>>> GetClosures(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken) =>
        Ok(await _inventory.GetClosuresAsync(from, to, cancellationToken));

    [HttpPost("closures")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<InventoryClosureDto>> CreateClosure(
        [FromBody] CreateInventoryClosureRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _inventory.CreateClosureAsync(
            request, GetActorId(), cancellationToken));

    [HttpDelete("closures/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> DeactivateClosure(
        Guid id,
        CancellationToken cancellationToken)
    {
        await _inventory.DeactivateClosureAsync(id, GetActorId(), cancellationToken);
        return NoContent();
    }

    [HttpPut("bookings/{bookingId:guid}/rooms/{reservationRoomId:guid}/assignment")]
    [Authorize(Policy = HotelAuthorization.ReservationsManage)]
    public async Task<ActionResult<ReservationRoomDto>> AssignRoom(
        Guid bookingId,
        Guid reservationRoomId,
        [FromBody] AssignReservationRoomRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _inventory.AssignRoomAsync(
            bookingId, reservationRoomId, request, GetActorId(), cancellationToken));

    private Guid GetActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)
            ? actorId
            : throw new UnauthorizedAccessException("The authenticated actor is invalid.");

    private static PublicRoomTypeDto ToPublicRoomType(RoomTypeDto roomType) => new(
        roomType.Id,
        roomType.Code,
        roomType.Name,
        roomType.Category,
        roomType.BaseOccupancy,
        roomType.MaxOccupancy,
        roomType.BasePricePerNight,
        roomType.Description,
        roomType.Amenities);
}
