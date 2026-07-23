using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed class RotateCredentialsRequest
{
    [Required, StringLength(128)]
    public string OldPassword { get; init; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 8)]
    public string NewPassword { get; init; } = string.Empty;

    [Required, Compare(nameof(NewPassword))]
    public string ConfirmNewPassword { get; init; } = string.Empty;
}
