namespace MooreHotels.Domain.Entities;

/// <summary>
/// A durable transactional email. Payloads contain only the minimum fields
/// needed to render one message and are deleted after successful delivery.
/// </summary>
public sealed class EmailOutboxMessage
{
    public Guid Id { get; set; }
    public string Template { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string ProtectedPayload { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public Guid? LockId { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
