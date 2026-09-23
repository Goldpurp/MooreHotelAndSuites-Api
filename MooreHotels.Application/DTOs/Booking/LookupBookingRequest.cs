using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record LookupBookingRequest(
    [Required, StringLength(30, MinimumLength = 4)] string Code,
    [EmailAddress, StringLength(254)] string? Email = null);

public sealed record AdminCancelBookingRequest(
    [StringLength(500)] string? Reason = null);
