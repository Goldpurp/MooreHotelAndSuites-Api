using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Services;

public interface IStaffService
{
    Task<StaffDashboardStatsDto> GetStaffStatsAsync();
    Task<IEnumerable<StaffSummaryDto>> GetAllStaffAsync();
    Task<IEnumerable<StaffSummaryDto>> GetAllUsersAsync(); 
    Task OnboardUserAsync(OnboardUserRequest request, Guid actingUserId);
    Task UpdateUserAsync(Guid userId, UpdateStaffRequest request, Guid actingUserId);
    // Task ToggleUserStatusAsync(Guid userId, Guid actingUserId);
    Task ChangeUserStatusAsync(Guid userId, ProfileStatus newStatus, Guid actingUserId);

    // Task ActivateUserAsync(Guid userId); 
    // Task DeactivateUserAsync(Guid userId); 
    Task DeleteUserAsync(Guid userId, Guid actingUserId);
}
