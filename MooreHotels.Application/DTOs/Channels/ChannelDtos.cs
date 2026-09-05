using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public sealed record DistributionChannelDto(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    DateTime UpdatedAtUtc);

public sealed record SaveDistributionChannelRequest(
    [Required, StringLength(40, MinimumLength = 2)] string Code,
    [Required, StringLength(120, MinimumLength = 2)] string Name,
    bool IsActive);

public sealed record ReceiveChannelEventRequest(
    [Required, StringLength(80)] string EventType,
    [Required, StringLength(160, MinimumLength = 8)] string IdempotencyKey,
    [StringLength(160)] string? ExternalReservationId,
    [Required] string PayloadJson,
    DateTime? OccurredAtUtc);

public sealed record ChannelEventDto(
    Guid Id,
    Guid ChannelId,
    string ChannelCode,
    ChannelEventDirection Direction,
    ChannelEventStatus Status,
    string EventType,
    string IdempotencyKey,
    string? ExternalReservationId,
    string PayloadJson,
    int AttemptCount,
    string? LastError,
    DateTime OccurredAtUtc,
    DateTime CreatedAtUtc,
    DateTime? ProcessedAtUtc);

public sealed record UpdateChannelEventRequest(
    ChannelEventStatus Status,
    [StringLength(1000)] string? Error);

public sealed record LinkChannelReservationRequest(
    [Required, StringLength(160)] string ExternalReservationId,
    [Required] Guid BookingId);

public sealed record ChannelReservationMappingDto(
    Guid Id,
    Guid ChannelId,
    string ChannelCode,
    string ExternalReservationId,
    Guid BookingId,
    string BookingCode,
    DateTime LinkedAtUtc);

public sealed record QueueChannelInventoryRequest(
    [Required] Guid RoomTypeId,
    [Required] DateOnly FromDate,
    [Required] DateOnly ToDate,
    [Required, StringLength(160, MinimumLength = 8)] string IdempotencyKey);

public sealed record ChannelReconciliationDto(
    Guid ChannelId,
    string ChannelCode,
    int PendingEvents,
    int FailedEvents,
    int DeadLetterEvents,
    int UnmappedReservationEvents,
    DateTime? OldestPendingAtUtc,
    DateTime GeneratedAtUtc);
