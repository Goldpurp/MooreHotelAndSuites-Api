using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.Application.Services;

public class OperationService : IOperationService
{
    private readonly IOperationLedgerRepository _ledgerRepo;
    private readonly IAnalyticsService _analyticsService;

    public OperationService(
        IOperationLedgerRepository ledgerRepo,
        IAnalyticsService analyticsService)
    {
        _ledgerRepo = ledgerRepo;
        _analyticsService = analyticsService;
    }

    public async Task<IEnumerable<OperationLogEntryDto>> GetLedgerAsync(
        string? filter = null,
        string? search = null,
        int limit = 200,
        DateTime? beforeUtc = null,
        Guid? beforeId = null,
        CancellationToken cancellationToken = default) =>
        await _ledgerRepo.GetLedgerAsync(
            filter,
            search,
            limit,
            beforeUtc,
            beforeId,
            cancellationToken);

    public async Task<DashboardKpis> GetOperationalKpisAsync()
    {
        var overview = await _analyticsService.GetOverviewAsync();
        return overview.Kpis;
    }
}
