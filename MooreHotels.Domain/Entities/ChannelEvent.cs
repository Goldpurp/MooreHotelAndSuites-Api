using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class ChannelEvent
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public ChannelEventDirection Direction { get; set; }
    public ChannelEventStatus Status { get; set; } = ChannelEventStatus.Pending;
    public string EventType { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? ExternalReservationId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public DistributionChannel? Channel { get; set; }
}
