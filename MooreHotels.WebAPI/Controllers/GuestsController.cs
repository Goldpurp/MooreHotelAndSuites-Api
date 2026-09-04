using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/guests")]
[Authorize(Roles = "Admin,Manager,Staff")]
public class GuestsController : ControllerBase
{
    private readonly IGuestService _guestService;
    public GuestsController(IGuestService guestService) => _guestService = guestService;

    [HttpGet]
    public async Task<IActionResult> GetGuests(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
    {
        return Ok(await _guestService.GetPagedGuestsAsync(page, pageSize, search));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetGuest(string id)
    {
        var dto = await _guestService.GetGuestByIdAsync(id);
        return dto == null ? NotFound() : Ok(dto);
    }
}