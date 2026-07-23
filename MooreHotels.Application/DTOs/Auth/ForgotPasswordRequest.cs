using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public record ForgotPasswordRequest(
    [Required, EmailAddress, StringLength(254)] string Email);
