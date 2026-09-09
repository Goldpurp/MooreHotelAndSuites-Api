using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record RequestBookingAccessLinkRequest(
    [Required, StringLength(30, MinimumLength = 4)] string BookingCode,
    [Required, EmailAddress, StringLength(254)] string Email);
