using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class FolioEntry
{
    public Guid Id { get; set; }
    public Guid FolioId { get; set; }
    public FolioEntryType Type { get; set; }
    public FolioEntryDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "NGN";
    public string Description { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string? SourceId { get; set; }
    public string? ExternalReference { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? ReversesEntryId { get; set; }
    public DateTime PostedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? PostedByUserId { get; set; }
    public string? Notes { get; set; }
    public Folio? Folio { get; set; }
    public FolioEntry? ReversesEntry { get; set; }
    public ApplicationUser? PostedByUser { get; set; }
}
