using Microsoft.AspNetCore.Identity;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Entities;

public class ApplicationUser : IdentityUser<Guid>
{
    public string Name { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public ProfileStatus Status { get; set; } = ProfileStatus.Active;
    public string? AvatarUrl { get; set; }
    public string? AvatarPublicId { get; set; }
    public string? Department { get; set; } // Categorization: Housekeeping, Reception, etc.
    public string? GuestId { get; set; }
    public Guest? GuestProfile { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
