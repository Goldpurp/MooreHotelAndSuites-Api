namespace MooreHotels.Domain.Entities;

public class Guest
{
    public string Id { get; set; } = string.Empty; // GS-XXXX
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AnonymizedAtUtc { get; set; }

    // Relationships
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
