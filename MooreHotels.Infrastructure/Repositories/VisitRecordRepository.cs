
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
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

    public async Task<PagedResult<VisitRecord>> GetPagedRecordsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPage = Math.Clamp(pageNumber, 1, 1_000_000);
        var normalizedSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.VisitRecords.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(v =>
                EF.Functions.ILike(v.GuestName, $"%{s}%") ||
                EF.Functions.ILike(v.GuestId, $"%{s}%") ||
                EF.Functions.ILike(v.BookingCode, $"%{s}%") ||
                v.RoomNumber.Contains(s) ||
                v.AuthorizedBy.Contains(s));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(v => v.Timestamp)
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(cancellationToken);

        return PagedResult<VisitRecord>.Create(items, totalCount, normalizedPage, normalizedSize);
    }

    public async Task AddAsync(VisitRecord record)
    {
        await _db.VisitRecords.AddAsync(record);
        await _db.SaveChangesAsync();
    }
}
