using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Repositories;

public class GuestRepository : IGuestRepository
{
    private readonly MooreHotelsDbContext _db;
    public GuestRepository(MooreHotelsDbContext db) => _db = db;

    public async Task<Guest?> GetByIdAsync(string id) => await _db.Guests.FindAsync(id);

    public async Task<Guest?> GetByEmailAndNameAsync(string email, string firstName, string lastName) =>
        await _db.Guests.FirstOrDefaultAsync(g =>
            g.MergedIntoGuestId == null &&
            (g.NormalizedEmail == email || g.Email == email) &&
            EF.Functions.ILike(g.FirstName, firstName) &&
            EF.Functions.ILike(g.LastName, lastName));

    public async Task<IEnumerable<Guest>> GetAllAsync() => await _db.Guests
        .AsNoTracking()
        .OrderByDescending(guest => guest.CreatedAt)
        .Take(1000)
        .ToListAsync();

    public async Task<IEnumerable<Guest>> SearchAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return await GetAllAsync();

        var t = term.Trim();
        return await _db.Guests
            .AsNoTracking()
            .Where(g => EF.Functions.ILike(g.Id, $"%{t}%") ||
                        EF.Functions.ILike(g.FirstName, $"%{t}%") ||
                        EF.Functions.ILike(g.LastName, $"%{t}%") ||
                        EF.Functions.ILike(g.Email, $"%{t}%") ||
                        g.Phone.Contains(t))
            .OrderByDescending(guest => guest.CreatedAt)
            .Take(200)
            .ToListAsync();
    }

    public async Task<PagedResult<Guest>> GetPagedGuestsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPage = Math.Max(1, pageNumber);
        var normalizedSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Guests.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            query = query.Where(g =>
                EF.Functions.ILike(g.Id, $"%{t}%") ||
                EF.Functions.ILike(g.FirstName, $"%{t}%") ||
                EF.Functions.ILike(g.LastName, $"%{t}%") ||
                EF.Functions.ILike(g.Email, $"%{t}%") ||
                g.Phone.Contains(t));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(guest => guest.CreatedAt)
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(cancellationToken);

        return PagedResult<Guest>.Create(items, totalCount, normalizedPage, normalizedSize);
    }

    public async Task AddAsync(Guest guest)
    {
        await _db.Guests.AddAsync(guest);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Guest guest)
    {
        _db.Guests.Update(guest);
        await _db.SaveChangesAsync();
    }
}
