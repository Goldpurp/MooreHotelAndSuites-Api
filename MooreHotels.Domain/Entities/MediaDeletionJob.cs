namespace MooreHotels.Domain.Entities;

public sealed class MediaDeletionJob
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public Guid? LockId { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
