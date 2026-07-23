using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public record UpdateProfileRequest(
    [StringLength(160, MinimumLength = 1)] string? FullName = null,
    [EmailAddress, StringLength(254)] string? Email = null,
    [Phone, StringLength(30)] string? Phone = null,
    [Url, StringLength(2048)] string? AvatarUrl = null);
