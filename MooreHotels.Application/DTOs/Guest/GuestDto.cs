namespace MooreHotels.Application.DTOs;

public record GuestDto(
    string Id,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string? AvatarUrl,
    DateTime JoinedDate);