namespace MooreHotels.Application.DTOs;

public sealed record RetryQueueItemDto(
    Guid Id, string Reference, string SourceType, int AttemptCount,
    string? LastErrorCode, DateTime CreatedAtUtc, bool CanRetry);
