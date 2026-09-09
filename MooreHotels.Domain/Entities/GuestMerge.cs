namespace MooreHotels.Domain.Entities;

public sealed class GuestMerge
{
    public Guid Id { get; set; }
    public string PrimaryGuestId { get; set; } = string.Empty;
    public string DuplicateGuestId { get; set; } = string.Empty;
    public string EvidenceType { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int MovedBookingCount { get; set; }
    public DateTime MergedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid MergedByUserId { get; set; }
    public Guest? PrimaryGuest { get; set; }
    public Guest? DuplicateGuest { get; set; }
    public ApplicationUser? MergedByUser { get; set; }
}
