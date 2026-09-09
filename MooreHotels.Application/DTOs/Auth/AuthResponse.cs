
namespace MooreHotels.Application.DTOs;

public record AuthResponse(
    string Token,
    string Email,
    string Name,
    string Role,
    bool MfaSetupRequired = false);
