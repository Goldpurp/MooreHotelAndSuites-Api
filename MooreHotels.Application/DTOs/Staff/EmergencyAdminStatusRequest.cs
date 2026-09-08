using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record EmergencyAdminStatusRequest(
    [Required, StringLength(128, MinimumLength = 1)] string CurrentPassword,
    [Required, StringLength(16, MinimumLength = 6)] string AuthenticatorCode,
    [Required, StringLength(320, MinimumLength = 10)] string Confirmation,
    [Required, StringLength(500, MinimumLength = 10)] string Reason);
