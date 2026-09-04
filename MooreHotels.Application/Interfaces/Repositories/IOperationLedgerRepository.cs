using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IOperationLedgerRepository
{
    Task<IReadOnlyList<OperationLogEntryDto>> GetLedgerAsync(
        string? filter,
        string? search,
        int limit,
        DateTime? beforeUtc,
        Guid? beforeId,
        CancellationToken cancellationToken = default);
}
