using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Domain.Common;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Repositories;

public class RoomRepository : IRoomRepository
{
    private readonly MooreHotelsDbContext _db;
    public RoomRepository(MooreHotelsDbContext db) => _db = db;

    public async Task<Room?> GetByIdAsync(Guid id) => await _db.Rooms
        .Include(room => room.RoomType)
        .FirstOrDefaultAsync(room => room.Id == id);

    public async Task<Room?> GetByRoomNumberAsync(string roomNumber) =>
        await _db.Rooms.FirstOrDefaultAsync(r => r.RoomNumber == roomNumber);

    public async Task<RoomType?> GetRoomTypeByIdAsync(Guid id) =>
        await _db.RoomTypes.FirstOrDefaultAsync(type => type.Id == id);

    public async Task<RoomType?> GetDefaultRoomTypeForCategoryAsync(RoomCategory category) =>
        await _db.RoomTypes
            .Where(type => type.Category == category && type.IsActive)
            .OrderBy(type => type.Code)
            .FirstOrDefaultAsync();

    public async Task<RoomType?> GetAnyRoomTypeForCategoryAsync(RoomCategory category) =>
        await _db.RoomTypes
            .Where(type => type.Category == category)
            .OrderBy(type => type.Code)
            .FirstOrDefaultAsync();

    public async Task<IEnumerable<Room>> GetAllAsync(bool onlyOnline = true)
    {
        var query = _db.Rooms.Include(r => r.Images).Include(r => r.RoomType).AsNoTracking().AsQueryable();
        if (onlyOnline) query = query.Where(r => r.IsOnline && r.RoomType != null &&
            r.RoomType.IsActive && r.Status != RoomStatus.Maintenance &&
            r.Status != RoomStatus.OutOfOrder);
        return await query.OrderBy(room => room.RoomNumber).ToListAsync();
    }

    public async Task<IEnumerable<Room>> SearchAsync(
        DateTime? checkIn,
        DateTime? checkOut,
        RoomCategory? category,
        int? capacity,
        string? roomNumber,
        string? amenity)
    {
        var query = _db.Rooms.Include(r => r.Images).Include(r => r.RoomType).AsNoTracking().AsQueryable();

        if (checkIn.HasValue && checkOut.HasValue)
        {
            var start = checkIn.Value;
            var end = checkOut.Value;
            var expirationCutoffUtc = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
            var bookedRoomIds = await _db.ReservationRooms
                .Where(item => item.AssignedRoomId.HasValue && item.Booking != null &&
                               item.Booking.Status != BookingStatus.Cancelled &&
                               item.Booking.Status != BookingStatus.CheckedOut &&
                               item.Booking.Status != BookingStatus.NoShow &&
                               !(item.Booking.Status == BookingStatus.Pending &&
                                 (item.Booking.PaymentStatus == PaymentStatus.Unpaid ||
                                  item.Booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                                 item.Booking.CreatedAt <= expirationCutoffUtc) &&
                               item.Booking.CheckIn < end && item.Booking.CheckOut > start)
                .Select(item => item.AssignedRoomId!.Value)
                .Distinct()
                .ToListAsync();

            var startDate = DateOnly.FromDateTime(start);
            var endDate = DateOnly.FromDateTime(end);
            var closedRoomIds = await _db.RoomInventoryClosures
                .Where(closure => closure.IsActive && closure.RoomId.HasValue &&
                                  closure.StartDate < endDate && closure.EndDate > startDate)
                .Select(closure => closure.RoomId!.Value)
                .Distinct()
                .ToListAsync();

            query = query.Where(r => !bookedRoomIds.Contains(r.Id) && !closedRoomIds.Contains(r.Id));
        }

        if (!string.IsNullOrWhiteSpace(roomNumber))
            query = query.Where(r => r.RoomNumber.Contains(roomNumber));

        if (category.HasValue)
            query = query.Where(r => r.Category == category.Value);

        if (capacity.HasValue && capacity.Value > 0)
            query = query.Where(r => r.Capacity >= capacity.Value);

        query = query.Where(r => r.IsOnline && r.Status != RoomStatus.Maintenance &&
                                 r.Status != RoomStatus.OutOfOrder &&
                                 r.RoomType != null && r.RoomType.IsActive);

        var rooms = await query.ToListAsync();

        if (!string.IsNullOrWhiteSpace(amenity))
        {
            rooms = rooms.Where(r => r.Amenities.Any(a => a.Contains(amenity, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        return rooms;
    }

    public async Task AddAsync(Room room)
    {
        await _db.Rooms.AddAsync(room);
    }

    public async Task AddRoomTypeAsync(RoomType roomType)
    {
        await _db.RoomTypes.AddAsync(roomType);
    }

    public Task UpdateAsync(Room room)
    {
        // Service operations load rooms as tracked aggregates. Calling Update on
        // that aggregate recursively marks newly-added inventory periods as
        // Modified. Publishing an offline room then issues an UPDATE for a period
        // that has not been inserted yet and EF reports a concurrency conflict.
        // Preserve the states already assigned by change tracking. Detached
        // aggregates retain the repository's existing graph-update behavior.
        var entry = _db.Entry(room);
        if (entry.State == EntityState.Detached)
        {
            _db.Rooms.Update(room);
        }
        return Task.CompletedTask;
    }

    public void AddInventoryPeriod(RoomInventoryPeriod period) =>
        _db.RoomInventoryPeriods.Add(period);

    public void RemoveInventoryPeriod(RoomInventoryPeriod period) =>
        _db.RoomInventoryPeriods.Remove(period);

    public async Task DeleteAsync(Room room)
    {
        _db.Rooms.Remove(room);
        await _db.SaveChangesAsync();
    }

    public async Task<Room?> GetByIdWithImagesAsync(Guid id)
    {
        return await _db.Rooms
            .Include(r => r.Images)
            .Include(r => r.RoomType)
            .Include(r => r.InventoryPeriods)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<AssetStatusDistribution> GetAssetStatusDistributionAsync(CancellationToken cancellationToken = default)
    {
        var statuses = await _db.Rooms.AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var occupied = statuses.FirstOrDefault(s => s.Status == RoomStatus.Occupied)?.Count ?? 0;
        var available = statuses.FirstOrDefault(s => s.Status == RoomStatus.Available)?.Count ?? 0;
        var cleaning = statuses.FirstOrDefault(s => s.Status == RoomStatus.Cleaning)?.Count ?? 0;
        var maintenance = statuses.FirstOrDefault(s => s.Status == RoomStatus.Maintenance)?.Count ?? 0;
        var dirty = statuses.FirstOrDefault(s => s.Status == RoomStatus.Dirty)?.Count ?? 0;
        var clean = statuses.FirstOrDefault(s => s.Status == RoomStatus.Clean)?.Count ?? 0;
        var inspected = statuses.FirstOrDefault(s => s.Status == RoomStatus.Inspected)?.Count ?? 0;
        var outOfOrder = statuses.FirstOrDefault(s => s.Status == RoomStatus.OutOfOrder)?.Count ?? 0;

        return new AssetStatusDistribution(
            occupied, available, cleaning, maintenance, dirty, clean, inspected, outOfOrder);
    }

    public async Task<(int TotalRooms, int OccupiedRooms)> GetRoomCountsAsync(CancellationToken cancellationToken = default)
    {
        var total = await _db.Rooms.AsNoTracking().CountAsync(cancellationToken);
        var occupied = await _db.Rooms.AsNoTracking().CountAsync(r => r.Status == RoomStatus.Occupied, cancellationToken);
        return (total, occupied);
    }
}
