namespace MooreHotels.Domain.Entities;

public sealed class GuestNote
{
    public Guid Id { get; set; }
    public string GuestId { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsSensitive { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public Guest? Guest { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
}
