using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record SetupMfaRequest(
    [Required, StringLength(128, MinimumLength = 1)] string CurrentPassword);

public sealed record EnableMfaRequest(
    [Required, StringLength(16, MinimumLength = 6)] string Code,
    [Required, StringLength(128, MinimumLength = 1)] string CurrentPassword);
