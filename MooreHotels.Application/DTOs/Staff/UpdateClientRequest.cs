using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record UpdateClientRequest(
    [Required, StringLength(160, MinimumLength = 2)] string FullName,
    [Phone, StringLength(30)] string? Phone,
    [Required, StringLength(500, MinimumLength = 10)] string Reason);
