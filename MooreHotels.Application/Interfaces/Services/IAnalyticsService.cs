
using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IAnalyticsService
{
    Task<DashboardOverviewDto> GetOverviewAsync(string period = "month");
}
