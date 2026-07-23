using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public record RegisterRequest(
    [Required, StringLength(80, MinimumLength = 1)] string FirstName,
    [Required, StringLength(80, MinimumLength = 1)] string LastName,
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [Required, Phone, StringLength(30, MinimumLength = 7)] string Phone);
