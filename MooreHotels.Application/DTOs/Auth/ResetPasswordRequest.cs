using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed class ResetPasswordRequest
{
    [Required, StringLength(36, MinimumLength = 36)]
    public string UserId { get; init; } = string.Empty;

    [Required, StringLength(4096, MinimumLength = 16)]
    public string Token { get; init; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 12)]
    public string NewPassword { get; init; } = string.Empty;

    [Required, Compare(nameof(NewPassword))]
    public string ConfirmNewPassword { get; init; } = string.Empty;
}
