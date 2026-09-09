using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record LookupBookingRequest(
    [Required, StringLength(30, MinimumLength = 4)] string Code);

public sealed record AdminCancelBookingRequest(
    [StringLength(500)] string? Reason = null);
