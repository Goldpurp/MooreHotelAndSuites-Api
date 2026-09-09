using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;
using MooreHotels.Application.Common;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using MooreHotels.WebAPI.Extensions;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/addons")]
public class AddOnsController : ControllerBase
{
    private readonly IAddOnService _addOnService;

    public AddOnsController(IAddOnService addOnService) => _addOnService = addOnService;

    [HttpGet]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public async Task<ActionResult<IReadOnlyList<PublicAddOnServiceDto>>> GetAll(
        [FromQuery] AddOnCategory? category = null,
        CancellationToken cancellationToken = default)
    {
        var services = await _addOnService.GetAllServicesAsync(
            true,
            category,
            cancellationToken);
        return Ok(services.Select(ToPublic).ToArray());
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public async Task<ActionResult<PublicAddOnServiceDto>> GetById(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var dto = await _addOnService.GetServiceByIdAsync(id, cancellationToken);
        return dto is not { IsActive: true } ? NotFound() : Ok(ToPublic(dto));
    }

    [HttpGet("management")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetManaged(
        [FromQuery] bool includeInactive = true,
        [FromQuery] AddOnCategory? category = null,
        CancellationToken cancellationToken = default) =>
        Ok(await _addOnService.GetAllServicesAsync(
            !includeInactive,
            category,
            cancellationToken));

    [HttpGet("management/{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetManagedById(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var dto = await _addOnService.GetServiceByIdAsync(id, cancellationToken);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Create([FromBody] CreateAddOnServiceRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _addOnService.CreateServiceAsync(
            request,
            GetActorId(),
            cancellationToken);
        return CreatedAtAction(nameof(GetManagedById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAddOnServiceRequest request, CancellationToken cancellationToken = default) =>
        Ok(await _addOnService.UpdateServiceAsync(
            id,
            request,
            GetActorId(),
            cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        await _addOnService.DeleteServiceAsync(id, GetActorId(), cancellationToken);
        return NoContent();
    }

    [HttpGet("bookings/{bookingCode}")]
    [Authorize(Policy = HotelAuthorization.ReservationsRead)]
    public async Task<IActionResult> GetBookingAddOns(string bookingCode, CancellationToken cancellationToken = default) =>
        Ok(await _addOnService.GetBookingAddOnsAsync(bookingCode, cancellationToken));

    [HttpPost("bookings/{bookingCode}")]
    [Authorize(Policy = HotelAuthorization.FolioIncidentals)]
    public async Task<IActionResult> AddServiceToBooking(
        string bookingCode,
        [FromBody] AddServiceToBookingRequest request,
        CancellationToken cancellationToken = default) =>
        Ok(await _addOnService.AddServiceToBookingAsync(
            bookingCode, request, GetActorId(), cancellationToken));

    private Guid GetActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)
            ? actorId
            : throw new UnauthorizedAccessException("The authenticated actor is invalid.");

    private static PublicAddOnServiceDto ToPublic(AddOnServiceDto service) => new(
        service.Id,
        service.Name,
        service.Description,
        service.Category,
        service.Price);
}
