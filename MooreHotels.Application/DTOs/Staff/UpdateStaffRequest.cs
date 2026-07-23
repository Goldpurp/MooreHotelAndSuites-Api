using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public sealed class UpdateStaffRequest
{
    [Required, StringLength(160, MinimumLength = 2)]
    public string FullName { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = string.Empty;

    [Phone, StringLength(30)]
    public string? Phone { get; init; }

    public UserRole AssignedRole { get; init; }

    [StringLength(80)]
    public string? Department { get; init; }
}
