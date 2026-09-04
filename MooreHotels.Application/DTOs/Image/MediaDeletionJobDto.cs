namespace MooreHotels.Application.DTOs;

public sealed record MediaDeletionJobDto(
    Guid Id,
    string PublicId,
    string SourceType,
    string SourceId,
    int AttemptCount,
    DateTime NextAttemptAtUtc,
    string? LastErrorCode,
    DateTime CreatedAtUtc);
