using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/folios")]
public sealed class FoliosController : ControllerBase
{
    private readonly IFolioService _folios;

    public FoliosController(IFolioService folios) => _folios = folios;

    [HttpGet("{bookingCode}")]
    [Authorize(Policy = HotelAuthorization.FolioRead)]
    public async Task<ActionResult<FolioDto>> Get(
        string bookingCode,
        CancellationToken cancellationToken) =>
        Ok(await _folios.GetByBookingCodeAsync(bookingCode, cancellationToken));

    [HttpPost("{bookingCode}/charges")]
    [Authorize(Policy = HotelAuthorization.FolioCharge)]
    public async Task<ActionResult<FolioDto>> PostCharge(
        string bookingCode,
        [FromBody] PostFolioChargeRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _folios.PostChargeAsync(
            bookingCode, request, GetActorId(), cancellationToken));

    [HttpPost("{bookingCode}/payments")]
    [Authorize(Policy = HotelAuthorization.FolioPayment)]
    public async Task<ActionResult<FolioDto>> PostPayment(
        string bookingCode,
        [FromBody] PostFolioPaymentRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _folios.PostPaymentAsync(
            bookingCode, request, GetActorId(), cancellationToken));

    [HttpPost("{bookingCode}/credits")]
    [Authorize(Policy = HotelAuthorization.FolioAdjust)]
    public async Task<ActionResult<FolioDto>> PostCredit(
        string bookingCode,
        [FromBody] PostFolioCreditRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _folios.PostCreditAsync(
            bookingCode, request, GetActorId(), cancellationToken));

    [HttpPost("{bookingCode}/entries/{entryId:guid}/void")]
    [Authorize(Policy = HotelAuthorization.FolioAdjust)]
    public async Task<ActionResult<FolioDto>> VoidEntry(
        string bookingCode,
        Guid entryId,
        [FromBody] VoidFolioEntryRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _folios.VoidEntryAsync(
            bookingCode, entryId, request, GetActorId(), cancellationToken));

    [HttpPost("{bookingCode}/close")]
    [Authorize(Policy = HotelAuthorization.FolioClose)]
    public async Task<ActionResult<FolioDto>> Close(
        string bookingCode,
        CancellationToken cancellationToken) =>
        Ok(await _folios.CloseAsync(bookingCode, GetActorId(), cancellationToken));

    private Guid GetActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)
            ? actorId
            : throw new UnauthorizedAccessException("The authenticated actor is invalid.");
}
