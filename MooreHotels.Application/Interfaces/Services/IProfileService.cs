using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IProfileService
{
    Task<UserProfileDto> GetProfileAsync(Guid userId);
    Task UpdateProfileAsync(Guid userId, UpdateProfileRequest request);
    Task RotateCredentialsAsync(Guid userId, RotateCredentialsRequest request);
    Task<IEnumerable<PublicBookingDto>> GetBookingHistoryAsync(Guid userId);
    Task DeactivateAccountAsync(Guid userId);
    Task ActivateAccountAsync(Guid userId);
}
