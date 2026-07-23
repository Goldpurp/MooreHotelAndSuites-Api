
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Repositories;

public class VisitRecordRepository : IVisitRecordRepository
{
    private readonly MooreHotelsDbContext _db;
    public VisitRecordRepository(MooreHotelsDbContext db) => _db = db;

    public async Task<IEnumerable<VisitRecord>> GetAllAsync() => 
        await _db.VisitRecords
            .AsNoTracking()
            .OrderByDescending(v => v.Timestamp)
            .Take(1000)
            .ToListAsync();

    public async Task AddAsync(VisitRecord record)
    {
        await _db.VisitRecords.AddAsync(record);
        await _db.SaveChangesAsync();
    }
}
