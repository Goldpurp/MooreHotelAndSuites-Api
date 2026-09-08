using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/reservations")]
[Authorize(Policy = HotelAuthorization.ReservationsRead)]
public sealed class ReservationOperationsController : ControllerBase
{
    private readonly IReservationAmendmentService _amendments;
    private readonly IPricingService _pricing;

    public ReservationOperationsController(
        IReservationAmendmentService amendments,
        IPricingService pricing)
    {
        _amendments = amendments;
        _pricing = pricing;
    }

    [HttpGet("{bookingId:guid}/amendments")]
    public async Task<ActionResult<IReadOnlyList<ReservationAmendmentDto>>> GetHistory(
        Guid bookingId, CancellationToken cancellationToken) =>
        Ok(await _amendments.GetHistoryAsync(bookingId, cancellationToken));

    [HttpPost("{bookingId:guid}/amendments")]
    [Authorize(Policy = HotelAuthorization.ReservationsManage)]
    public async Task<ActionResult<ReservationAmendmentDto>> Amend(
        Guid bookingId, [FromBody] AmendReservationRequest request, CancellationToken cancellationToken) =>
        Ok(await _amendments.AmendAsync(bookingId, request, ActorId(), cancellationToken));

    [HttpPost("{bookingId:guid}/amendment-quotes")]
    [Authorize(Policy = HotelAuthorization.ReservationsManage)]
    public async Task<ActionResult<PricingQuoteDto>> CreateAmendmentQuote(
        Guid bookingId,
        [FromBody] CreatePricingQuoteRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _pricing.CreateAmendmentQuoteAsync(
            bookingId, request, ActorId(), cancellationToken));

    private Guid ActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new UnauthorizedAccessException("The authenticated actor is invalid.");
}
