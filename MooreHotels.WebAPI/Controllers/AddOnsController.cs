using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/addons")]
public class AddOnsController : ControllerBase
{
    private readonly IAddOnService _addOnService;

    public AddOnsController(IAddOnService addOnService) => _addOnService = addOnService;

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool onlyActive = true,
        [FromQuery] AddOnCategory? category = null,
        CancellationToken cancellationToken = default) =>
        Ok(await _addOnService.GetAllServicesAsync(onlyActive, category, cancellationToken));

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        var dto = await _addOnService.GetServiceByIdAsync(id, cancellationToken);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Create([FromBody] CreateAddOnServiceRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _addOnService.CreateServiceAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAddOnServiceRequest request, CancellationToken cancellationToken = default) =>
        Ok(await _addOnService.UpdateServiceAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        await _addOnService.DeleteServiceAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("bookings/{bookingCode}")]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> GetBookingAddOns(string bookingCode, CancellationToken cancellationToken = default) =>
        Ok(await _addOnService.GetBookingAddOnsAsync(bookingCode, cancellationToken));

    [HttpPost("bookings/{bookingCode}")]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> AddServiceToBooking(
        string bookingCode,
        [FromBody] AddServiceToBookingRequest request,
        CancellationToken cancellationToken = default) =>
        Ok(await _addOnService.AddServiceToBookingAsync(bookingCode, request, cancellationToken));
}
