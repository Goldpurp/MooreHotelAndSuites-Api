using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/guest-crm")]
[Authorize(Policy = HotelAuthorization.GuestPiiRead)]
public sealed class GuestCrmController : ControllerBase
{
    private readonly IGuestCrmService _service;
    public GuestCrmController(IGuestCrmService service) => _service = service;

    [HttpGet("{guestId}")]
    public async Task<ActionResult<GuestCrmProfileDto>> Get(string guestId, CancellationToken ct) =>
        Ok(await _service.GetProfileAsync(guestId, IsManagement(), ct));

    [HttpGet("{guestId}/duplicates")]
    [Authorize(Policy = HotelAuthorization.GuestCrmManage)]
    public async Task<ActionResult<IReadOnlyList<GuestDuplicateCandidateDto>>> Duplicates(
        string guestId, CancellationToken ct) => Ok(await _service.FindDuplicatesAsync(guestId, ct));

    [HttpPut("{guestId}/preferences")]
    public async Task<ActionResult<GuestPreferencesDto>> Preferences(
        string guestId, [FromBody] UpdateGuestPreferencesRequest request, CancellationToken ct) =>
        Ok(await _service.UpdatePreferencesAsync(guestId, request, ActorId(), ct));

    [HttpPost("{guestId}/notes")]
    [Authorize(Policy = HotelAuthorization.GuestCrmManage)]
    public async Task<ActionResult<GuestNoteDto>> AddNote(
        string guestId, [FromBody] AddGuestNoteRequest request, CancellationToken ct) =>
        Ok(await _service.AddNoteAsync(guestId, request, ActorId(), ct));

    [HttpPost("{guestId}/verify-contact")]
    [Authorize(Policy = HotelAuthorization.GuestCrmManage)]
    public async Task<IActionResult> Verify(
        string guestId, [FromBody] VerifyGuestContactRequest request, CancellationToken ct)
    {
        await _service.VerifyContactAsync(guestId, request, ActorId(), ct);
        return NoContent();
    }

    [HttpPost("merge")]
    [Authorize(Policy = HotelAuthorization.GuestCrmManage)]
    public async Task<ActionResult<GuestMergeDto>> Merge(
        [FromBody] MergeGuestRequest request, CancellationToken ct) =>
        Ok(await _service.MergeAsync(request, ActorId(), ct));

    private bool IsManagement() => User.IsInRole("Admin") || User.IsInRole("Manager");
    private Guid ActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : throw new UnauthorizedAccessException("The authenticated actor is invalid.");
}
