using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IChannelManagementService
{
    Task<IReadOnlyList<DistributionChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default);
    Task<DistributionChannelDto> SaveChannelAsync(Guid? id, SaveDistributionChannelRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<ChannelEventDto> ReceiveEventAsync(string channelCode, ReceiveChannelEventRequest request, CancellationToken cancellationToken = default);
    Task<ChannelEventDto> QueueInventoryAsync(Guid channelId, QueueChannelInventoryRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<ChannelEventDto> UpdateEventAsync(Guid eventId, UpdateChannelEventRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<ChannelReservationMappingDto> LinkReservationAsync(Guid channelId, LinkChannelReservationRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<ChannelReconciliationDto> GetReconciliationAsync(Guid channelId, CancellationToken cancellationToken = default);
}
