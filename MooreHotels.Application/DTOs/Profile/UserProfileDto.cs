namespace MooreHotels.Application.DTOs;

public record UserProfileDto(
    Guid Id,
    string Name,
    string Email,
    string? Phone,
    string Role,
    string Status,
    string? AvatarUrl,
    bool EmailVerified,
    DateTime CreatedAt,
    string? GuestId,
    string? Department);
