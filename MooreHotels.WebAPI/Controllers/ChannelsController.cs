using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/channels")]
[Authorize(Policy = HotelAuthorization.ChannelsManage)]
public sealed class ChannelsController : ControllerBase
{
    private readonly IChannelManagementService _service;
    public ChannelsController(IChannelManagementService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DistributionChannelDto>>> Get(CancellationToken ct) =>
        Ok(await _service.GetChannelsAsync(ct));

    [HttpPost]
    public async Task<ActionResult<DistributionChannelDto>> Create(
        [FromBody] SaveDistributionChannelRequest request, CancellationToken ct) =>
        Ok(await _service.SaveChannelAsync(null, request, ActorId(), ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<DistributionChannelDto>> Update(
        Guid id, [FromBody] SaveDistributionChannelRequest request, CancellationToken ct) =>
        Ok(await _service.SaveChannelAsync(id, request, ActorId(), ct));

    [HttpPost("{channelCode}/events/inbound")]
    public async Task<ActionResult<ChannelEventDto>> Receive(
        string channelCode, [FromBody] ReceiveChannelEventRequest request, CancellationToken ct) =>
        Ok(await _service.ReceiveEventAsync(channelCode, request, ct));

    [HttpPost("{channelId:guid}/inventory-events")]
    public async Task<ActionResult<ChannelEventDto>> QueueInventory(
        Guid channelId, [FromBody] QueueChannelInventoryRequest request, CancellationToken ct) =>
        Ok(await _service.QueueInventoryAsync(channelId, request, ActorId(), ct));

    [HttpPut("events/{eventId:guid}")]
    public async Task<ActionResult<ChannelEventDto>> UpdateEvent(
        Guid eventId, [FromBody] UpdateChannelEventRequest request, CancellationToken ct) =>
        Ok(await _service.UpdateEventAsync(eventId, request, ActorId(), ct));

    [HttpPost("{channelId:guid}/reservation-mappings")]
    public async Task<ActionResult<ChannelReservationMappingDto>> LinkReservation(
        Guid channelId, [FromBody] LinkChannelReservationRequest request, CancellationToken ct) =>
        Ok(await _service.LinkReservationAsync(channelId, request, ActorId(), ct));

    [HttpGet("{channelId:guid}/reconciliation")]
    public async Task<ActionResult<ChannelReconciliationDto>> Reconciliation(
        Guid channelId, CancellationToken ct) => Ok(await _service.GetReconciliationAsync(channelId, ct));

    private Guid ActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : throw new UnauthorizedAccessException("The authenticated actor is invalid.");
}
