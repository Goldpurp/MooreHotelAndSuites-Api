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
    public string? DataSubjectGuestId { get; set; }
    public string ProtectedPayload { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public Guid? LockId { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? QuarantinedAtUtc { get; set; }
    public string? DeliveryFailureMetadataJson { get; set; }

    /// <summary>
    /// Set once the provider has accepted this message. The outbox row is
    /// normally deleted immediately after that in the same worker pass; this
    /// field exists only for the rare case where the delete itself fails
    /// (e.g. a transient DB error right after a successful send), so a later
    /// retry can finish cleanup without re-sending through the provider -
    /// whose own idempotency header is not a documented, provider-enforced
    /// dedup guarantee we can rely on alone.
    /// </summary>
    public DateTime? DeliveredAtUtc { get; set; }
}
