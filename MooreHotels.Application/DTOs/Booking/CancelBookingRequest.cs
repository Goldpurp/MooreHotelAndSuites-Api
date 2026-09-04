using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public record CancelBookingRequest(
    [Required, StringLength(30, MinimumLength = 4)] string BookingCode,
    [EmailAddress, StringLength(254)] string? Email,
    [StringLength(128, MinimumLength = 40)] string? GuestAccessToken = null,
    [StringLength(500)] string? Reason = null
);
