using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/admin/client-guest-links")]
[Authorize(Roles = "Admin")]
public sealed class ClientGuestReconciliationController : ControllerBase
{
    private readonly IClientGuestReconciliationService _service;

    public ClientGuestReconciliationController(IClientGuestReconciliationService service) =>
        _service = service;

    [HttpGet("issues")]
    public async Task<ActionResult<PagedResult<ClientGuestLinkIssueDto>>> GetIssues(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await _service.GetIssuesAsync(pageNumber, pageSize, cancellationToken));

    [HttpPost("{userId:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(
        Guid userId,
        [FromBody] ReconcileClientGuestRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actingUserId))
            return Unauthorized();

        return Ok(await _service.ReconcileAsync(
            userId,
            request,
            actingUserId,
            HttpContext.TraceIdentifier,
            cancellationToken));
    }
}
