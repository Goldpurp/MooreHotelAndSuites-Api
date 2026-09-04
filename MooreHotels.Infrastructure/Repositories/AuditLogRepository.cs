using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly MooreHotelsDbContext _db;
    public AuditLogRepository(MooreHotelsDbContext db) => _db = db;

    public async Task AddAsync(AuditLog log)
    {
        await _db.AuditLogs.AddAsync(log);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<AuditLog>> GetAllAsync() =>
        await _db.AuditLogs
            .AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Take(1000)
            .ToListAsync();

    public async Task<PagedResult<AuditLog>> GetPagedLogsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        string? entityType = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPage = Math.Max(1, pageNumber);
        var normalizedSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(l => l.EntityType == entityType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            var isGuid = Guid.TryParse(s, out var searchGuid);
            query = query.Where(l =>
                EF.Functions.ILike(l.Action, $"%{s}%") ||
                EF.Functions.ILike(l.EntityType, $"%{s}%") ||
                l.EntityId.Contains(s) ||
                (isGuid && l.ProfileId == searchGuid));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(cancellationToken);

        return PagedResult<AuditLog>.Create(items, totalCount, normalizedPage, normalizedSize);
    }
}
