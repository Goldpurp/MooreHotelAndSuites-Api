using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public sealed class PrivacyRequest
{
    public Guid Id { get; set; }
    public string GuestId { get; set; } = string.Empty;
    public Guid? RequestedByUserId { get; set; }
    public DataSubjectRequestType Type { get; set; }
    public DataSubjectRequestStatus Status { get; set; } = DataSubjectRequestStatus.Pending;
    public string? Details { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? ResolvedByUserId { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolutionNotes { get; set; }

    public Guest? Guest { get; set; }
    public ApplicationUser? RequestedByUser { get; set; }
    public ApplicationUser? ResolvedByUser { get; set; }
}
