
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog log);
    Task<IEnumerable<AuditLog>> GetAllAsync();
    Task<PagedResult<AuditLog>> GetPagedLogsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        string? entityType = null,
        string? search = null,
        CancellationToken cancellationToken = default);
}
