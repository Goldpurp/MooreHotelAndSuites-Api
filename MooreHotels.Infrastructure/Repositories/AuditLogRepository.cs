using Microsoft.EntityFrameworkCore;
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
}
