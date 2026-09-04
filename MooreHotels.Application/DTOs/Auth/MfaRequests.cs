using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record EnableMfaRequest(
    [Required, StringLength(16, MinimumLength = 6)] string Code);
