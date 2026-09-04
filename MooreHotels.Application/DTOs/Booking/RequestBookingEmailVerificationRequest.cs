using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record RequestBookingEmailVerificationRequest(
    [Required, EmailAddress, StringLength(254)] string Email);
