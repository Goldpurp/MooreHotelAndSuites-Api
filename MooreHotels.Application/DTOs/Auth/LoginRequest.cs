
using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public record LoginRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 1)] string Password);
