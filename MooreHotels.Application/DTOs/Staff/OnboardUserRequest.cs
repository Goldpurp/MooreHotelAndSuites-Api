using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record OnboardUserRequest(
    [Required, StringLength(160, MinimumLength = 2)] string FullName,
    [Required, EmailAddress, StringLength(254)] string Email,
    UserRole AssignedRole,
    ProfileStatus Status,
    [StringLength(80)] string? Department = null,
    [Phone, StringLength(30)] string? Phone = null);
