
using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IAuditService
{
    Task<IEnumerable<AuditLogDto>> GetAllLogsAsync();
    Task<PagedResult<AuditLogDto>> GetPagedLogsAsync(int pageNumber = 1, int pageSize = 20, string? entityType = null, string? search = null);
    Task LogActionAsync(Guid userId, string action, string entityType, string entityId, object? oldData = null, object? newData = null);
}
