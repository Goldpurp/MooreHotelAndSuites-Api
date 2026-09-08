using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record VerifyEmailRequest(
    [Required, StringLength(36, MinimumLength = 36)] string UserId,
    [Required, StringLength(4096, MinimumLength = 16)] string Token);
