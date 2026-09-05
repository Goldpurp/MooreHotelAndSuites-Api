namespace MooreHotels.Domain.Entities;

public class Guest
{
    public string Id { get; set; } = string.Empty; // GS-XXXX
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string NormalizedPhone { get; set; } = string.Empty;
    public DateTime? EmailVerifiedAtUtc { get; set; }
    public DateTime? PhoneVerifiedAtUtc { get; set; }
    public string PreferencesJson { get; set; } = "{}";
    public string? MergedIntoGuestId { get; set; }
    public DateTime? MergedAtUtc { get; set; }
    public Guid? MergedByUserId { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AnonymizedAtUtc { get; set; }

    // Relationships
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<GuestNote> Notes { get; set; } = new List<GuestNote>();
    public Guest? MergedIntoGuest { get; set; }
    public ApplicationUser? MergedByUser { get; set; }
}
