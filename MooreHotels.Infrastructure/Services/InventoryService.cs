using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Services;

public sealed class InventoryService : IInventoryService
{
    private readonly MooreHotelsDbContext _db;
    private readonly IHotelTimeService _hotelTime;

    public InventoryService(MooreHotelsDbContext db, IHotelTimeService hotelTime)
    {
        _db = db;
        _hotelTime = hotelTime;
    }

    public async Task<IReadOnlyList<RoomTypeDto>> GetRoomTypesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _db.RoomTypes.AsNoTracking();
        if (!includeInactive) query = query.Where(type => type.IsActive);
        return await query
            .OrderBy(type => type.Code)
            .Select(type => new RoomTypeDto(
                type.Id,
                type.Code,
                type.Name,
                type.Category,
                type.BaseOccupancy,
                type.MaxOccupancy,
                type.BasePricePerNight,
                type.Description,
                type.Amenities,
                type.IsActive,
                type.Rooms.Count,
                type.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<RoomTypeDto> SaveRoomTypeAsync(
        Guid? id,
        SaveRoomTypeRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireInventoryActorAsync(actorId, allowReservationsStaff: false, cancellationToken);
        if (id == Guid.Empty) throw new NotFoundException("Room type not found.");
        var code = NormalizeCode(request.Code);
        var name = RequireText(request.Name, "Room-type name", 120);
        var description = CleanText(request.Description, "Room-type description", 2000);
        var amenities = NormalizeAmenities(request.Amenities);
        if (!Enum.IsDefined(request.Category))
            throw new BadRequestException("Room category is invalid.");
        if (request.BaseOccupancy is < 1 or > 50 || request.MaxOccupancy is < 1 or > 50 ||
            request.BaseOccupancy > request.MaxOccupancy)
            throw new BadRequestException("Base occupancy cannot exceed maximum occupancy.");
        if (request.BasePricePerNight is <= 0 or > 9999999999999999m)
            throw new BadRequestException("Base nightly price is outside the supported range.");

        var type = id.HasValue
            ? await _db.RoomTypes.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
              ?? throw new NotFoundException("Room type not found.")
            : new RoomType { Id = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow };
        var expirationCutoff = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
        if (!request.IsActive && type.IsActive && await _db.ReservationRooms.AnyAsync(item =>
                item.RoomTypeId == type.Id &&
                item.Booking != null &&
                (item.Booking.Status == BookingStatus.CheckedIn || item.Booking.CheckOut > DateTime.UtcNow) &&
                item.Booking.Status != BookingStatus.Cancelled &&
                item.Booking.Status != BookingStatus.CheckedOut &&
                item.Booking.Status != BookingStatus.NoShow &&
                !(item.Booking.Status == BookingStatus.Pending &&
                  (item.Booking.PaymentStatus == PaymentStatus.Unpaid ||
                   item.Booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                  item.Booking.CreatedAt <= expirationCutoff),
                cancellationToken))
        {
            throw new BadRequestException(
                "A room type with active or future reservations cannot be deactivated.");
        }

        if (id.HasValue && await _db.Rooms.AnyAsync(room =>
                room.RoomTypeId == type.Id &&
                (room.Category != request.Category || room.Capacity > request.MaxOccupancy),
                cancellationToken))
        {
            throw new BadRequestException(
                "Update the physical rooms first; at least one room conflicts with the requested category or maximum occupancy.");
        }

        var oldData = id.HasValue ? JsonSerializer.Serialize(ToDto(type, 0)) : null;
        type.Code = code;
        type.Name = name;
        type.Category = request.Category;
        type.BaseOccupancy = request.BaseOccupancy;
        type.MaxOccupancy = request.MaxOccupancy;
        type.BasePricePerNight = FolioAccounting.Money(request.BasePricePerNight);
        type.Description = description;
        type.Amenities = amenities;
        type.IsActive = request.IsActive;
        type.UpdatedAtUtc = DateTime.UtcNow;
        if (!id.HasValue) _db.RoomTypes.Add(type);
        AddAudit(
            actorId,
            id.HasValue ? "ROOM_TYPE_UPDATED" : "ROOM_TYPE_CREATED",
            "RoomType",
            type.Id,
            oldData,
            JsonSerializer.Serialize(ToDto(type, 0)));
        await SaveConfigurationAsync(cancellationToken);
        var count = await _db.Rooms.CountAsync(room => room.RoomTypeId == type.Id, cancellationToken);
        return ToDto(type, count);
    }

    public Task<RoomTypeAvailabilityDto> GetAvailabilityAsync(
        Guid roomTypeId,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        int requestedUnits,
        CancellationToken cancellationToken = default) =>
        GetAvailabilityCoreAsync(
            roomTypeId, checkInDate, checkOutDate, requestedUnits, null, cancellationToken);

    public Task<RoomTypeAvailabilityDto> GetAvailabilityExcludingBookingAsync(
        Guid roomTypeId,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        int requestedUnits,
        Guid excludedBookingId,
        CancellationToken cancellationToken = default) =>
        GetAvailabilityCoreAsync(
            roomTypeId, checkInDate, checkOutDate, requestedUnits, excludedBookingId, cancellationToken);

    private async Task<RoomTypeAvailabilityDto> GetAvailabilityCoreAsync(
        Guid roomTypeId,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        int requestedUnits,
        Guid? excludedBookingId,
        CancellationToken cancellationToken)
    {
        if (roomTypeId == Guid.Empty || excludedBookingId == Guid.Empty)
            throw new BadRequestException("Room type or excluded booking identifier is invalid.");
        ValidateStay(checkInDate, checkOutDate, requestedUnits);
        var roomType = await _db.RoomTypes.AsNoTracking()
            .SingleOrDefaultAsync(type => type.Id == roomTypeId && type.IsActive, cancellationToken)
            ?? throw new NotFoundException("Room type not found or inactive.");
        var physicalRoomIds = await _db.Rooms.AsNoTracking()
            .Where(room => room.RoomTypeId == roomTypeId && room.IsOnline &&
                           room.Status != RoomStatus.Maintenance && room.Status != RoomStatus.OutOfOrder)
            .Select(room => room.Id)
            .ToArrayAsync(cancellationToken);
        var startUtc = _hotelTime.GetCheckInUtc(checkInDate.ToDateTime(TimeOnly.MinValue));
        var endUtc = _hotelTime.GetCheckOutUtc(checkOutDate.ToDateTime(TimeOnly.MinValue));
        var expirationCutoff = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
        var reservations = await _db.ReservationRooms.AsNoTracking()
            .Where(item =>
                item.RoomTypeId == roomTypeId &&
                (!excludedBookingId.HasValue || item.BookingId != excludedBookingId.Value) &&
                item.Booking != null &&
                item.Booking.CheckIn < endUtc &&
                item.Booking.CheckOut > startUtc &&
                item.Booking.Status != BookingStatus.Cancelled &&
                item.Booking.Status != BookingStatus.CheckedOut &&
                item.Booking.Status != BookingStatus.NoShow &&
                !(item.Booking.Status == BookingStatus.Pending &&
                  (item.Booking.PaymentStatus == PaymentStatus.Unpaid ||
                   item.Booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                  item.Booking.CreatedAt <= expirationCutoff))
            .Select(item => new { item.Booking!.CheckIn, item.Booking.CheckOut })
            .ToListAsync(cancellationToken);
        var closures = await _db.RoomInventoryClosures.AsNoTracking()
            .Where(closure =>
                closure.RoomTypeId == roomTypeId &&
                closure.IsActive &&
                closure.StartDate < checkOutDate &&
                closure.EndDate > checkInDate)
            .Select(closure => new
            {
                closure.RoomId,
                closure.StartDate,
                closure.EndDate,
                closure.Units
            })
            .ToListAsync(cancellationToken);

        var days = new List<InventoryDayDto>();
        for (var date = checkInDate; date < checkOutDate; date = date.AddDays(1))
        {
            var dayStart = _hotelTime.GetCheckInUtc(date.ToDateTime(TimeOnly.MinValue));
            var dayEnd = _hotelTime.GetCheckOutUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue));
            var reserved = reservations.Count(item => item.CheckIn < dayEnd && item.CheckOut > dayStart);
            var activeClosures = closures.Where(item => item.StartDate <= date && item.EndDate > date).ToArray();
            var closedPhysicalRooms = activeClosures
                .Where(item => item.RoomId.HasValue && physicalRoomIds.Contains(item.RoomId.Value))
                .Select(item => item.RoomId!.Value)
                .Distinct()
                .Count();
            var closedTypeUnits = activeClosures.Where(item => !item.RoomId.HasValue).Sum(item => item.Units);
            var closed = Math.Min(physicalRoomIds.Length, closedPhysicalRooms + closedTypeUnits);
            days.Add(new InventoryDayDto(
                date,
                physicalRoomIds.Length,
                closed,
                reserved,
                Math.Max(0, physicalRoomIds.Length - closed - reserved)));
        }

        var availableUnits = days.Count == 0 ? 0 : days.Min(day => day.AvailableUnits);
        return new RoomTypeAvailabilityDto(
            roomType.Id,
            roomType.Code,
            roomType.Name,
            checkInDate,
            checkOutDate,
            requestedUnits,
            availableUnits,
            availableUnits >= requestedUnits,
            days);
    }

    public async Task<IReadOnlyList<InventoryClosureDto>> GetClosuresAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        if (fromDate.HasValue && toDate.HasValue &&
            (toDate.Value <= fromDate.Value || toDate.Value.DayNumber - fromDate.Value.DayNumber > 731))
            throw new BadRequestException("Closure query dates are invalid or exceed two years.");
        var query = _db.RoomInventoryClosures.AsNoTracking()
            .Include(item => item.RoomType)
            .Include(item => item.Room)
            .AsQueryable();
        if (fromDate.HasValue) query = query.Where(item => item.EndDate > fromDate.Value);
        if (toDate.HasValue) query = query.Where(item => item.StartDate < toDate.Value);
        var closures = await query.OrderBy(item => item.StartDate).ThenBy(item => item.Id).Take(2000)
            .ToListAsync(cancellationToken);
        return closures.Select(ToClosureDto).ToArray();
    }

    public async Task<InventoryClosureDto> CreateClosureAsync(
        CreateInventoryClosureRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireInventoryActorAsync(actorId, allowReservationsStaff: false, cancellationToken);
        if (request.RoomTypeId == Guid.Empty || request.RoomId == Guid.Empty)
            throw new BadRequestException("Room type or room identifier is invalid.");
        if (request.Units is < 1 or > 1000)
            throw new BadRequestException("Closure units are invalid.");
        if (request.EndDate <= request.StartDate)
            throw new BadRequestException("Closure end date must be after its start date.");
        if (request.StartDate < _hotelTime.Today.AddDays(-1) ||
            request.EndDate > _hotelTime.Today.AddYears(2).AddDays(1))
            throw new BadRequestException("Inventory closures must fall within the supported two-year window.");
        var reason = RequireText(request.Reason, "Closure reason", 500);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            // Booking creation uses this same room-type key. The capacity
            // decision and insert therefore cannot race another booking or
            // closure on any API instance.
            var advisoryKey = BitConverter.ToInt64(request.RoomTypeId.ToByteArray(), 0);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey})",
                cancellationToken);

            var type = await _db.RoomTypes.SingleOrDefaultAsync(
                           item => item.Id == request.RoomTypeId,
                           cancellationToken)
                       ?? throw new NotFoundException("Room type not found.");
            Room? room = null;
            if (request.RoomId.HasValue)
            {
                room = await _db.Rooms.SingleOrDefaultAsync(
                           item => item.Id == request.RoomId,
                           cancellationToken)
                       ?? throw new NotFoundException("Physical room not found.");
                if (room.RoomTypeId != type.Id)
                    throw new BadRequestException("The physical room does not belong to the selected room type.");
                if (request.Units != 1)
                    throw new BadRequestException("A physical-room closure must contain exactly one unit.");
            }

            var physicalCount = await _db.Rooms.CountAsync(
                item => item.RoomTypeId == type.Id,
                cancellationToken);
            if (physicalCount == 0 || request.Units > physicalCount)
                throw new BadRequestException("Closure units exceed registered physical inventory.");
            var availability = await GetAvailabilityCoreAsync(
                type.Id, request.StartDate, request.EndDate, 1, null, cancellationToken);
            if (availability.Days.Any(day => day.AvailableUnits < request.Units))
                throw new ConflictException(
                    "The closure would overbook this room type. Move or amend reservations first.");
            if (room is not null)
            {
                if (await _db.RoomInventoryClosures.AnyAsync(closure =>
                        closure.RoomId == room.Id && closure.IsActive &&
                        closure.StartDate < request.EndDate && closure.EndDate > request.StartDate,
                        cancellationToken))
                    throw new ConflictException("The physical room already has an overlapping closure.");
                var startUtc = _hotelTime.GetCheckInUtc(request.StartDate.ToDateTime(TimeOnly.MinValue));
                var endUtc = _hotelTime.GetCheckOutUtc(request.EndDate.ToDateTime(TimeOnly.MinValue));
                var expirationCutoff = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
                if (await _db.ReservationRooms.AnyAsync(unit =>
                        unit.AssignedRoomId == room.Id && unit.Booking != null &&
                        unit.Booking.CheckIn < endUtc && unit.Booking.CheckOut > startUtc &&
                        unit.Booking.Status != BookingStatus.Cancelled &&
                        unit.Booking.Status != BookingStatus.CheckedOut &&
                        unit.Booking.Status != BookingStatus.NoShow &&
                        !(unit.Booking.Status == BookingStatus.Pending &&
                          (unit.Booking.PaymentStatus == PaymentStatus.Unpaid ||
                           unit.Booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                          unit.Booking.CreatedAt <= expirationCutoff),
                        cancellationToken))
                {
                    throw new ConflictException(
                        "Move the overlapping reservation before closing this physical room.");
                }
            }
            var now = DateTime.UtcNow;
            var closure = new RoomInventoryClosure
            {
                Id = Guid.NewGuid(),
                RoomTypeId = type.Id,
                RoomId = room?.Id,
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                Units = request.Units,
                Reason = reason,
                CreatedByUserId = actorId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.RoomInventoryClosures.Add(closure);
            AddAudit(actorId, "INVENTORY_CLOSURE_CREATED", "RoomInventoryClosure", closure.Id, null,
                JsonSerializer.Serialize(new
                {
                    closure.RoomTypeId,
                    closure.RoomId,
                    closure.StartDate,
                    closure.EndDate,
                    closure.Units,
                    closure.Reason
                }));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            closure.RoomType = type;
            closure.Room = room;
            return ToClosureDto(closure);
        });
    }

    public async Task DeactivateClosureAsync(
        Guid id,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireInventoryActorAsync(actorId, allowReservationsStaff: false, cancellationToken);
        if (id == Guid.Empty) throw new NotFoundException("Inventory closure not found.");
        var closure = await _db.RoomInventoryClosures.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                      ?? throw new NotFoundException("Inventory closure not found.");
        if (!closure.IsActive) return;
        var oldEndDate = closure.EndDate;
        if (closure.StartDate > _hotelTime.Today)
        {
            closure.IsActive = false;
        }
        else
        {
            var releaseDate = _hotelTime.Today.AddDays(1);
            if (releaseDate < closure.EndDate) closure.EndDate = releaseDate;
        }
        closure.UpdatedAtUtc = DateTime.UtcNow;
        AddAudit(actorId,
            closure.IsActive ? "INVENTORY_CLOSURE_ENDED" : "INVENTORY_CLOSURE_DEACTIVATED",
            "RoomInventoryClosure",
            closure.Id,
            JsonSerializer.Serialize(new { IsActive = true, EndDate = oldEndDate }),
            JsonSerializer.Serialize(new { closure.IsActive, closure.EndDate, closure.UpdatedAtUtc }));
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReservationRoomDto> AssignRoomAsync(
        Guid bookingId,
        Guid reservationRoomId,
        AssignReservationRoomRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireInventoryActorAsync(actorId, allowReservationsStaff: true, cancellationToken);
        if (bookingId == Guid.Empty || reservationRoomId == Guid.Empty || request.RoomId == Guid.Empty)
            throw new BadRequestException("Booking, reservation-room, or room identifier is invalid.");
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var booking = await _db.Bookings
                .FromSqlInterpolated($"SELECT * FROM bookings WHERE \"Id\" = {bookingId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Booking not found.");
            if (booking.Status is BookingStatus.Cancelled or BookingStatus.CheckedOut or BookingStatus.NoShow)
                throw new BadRequestException("Rooms cannot be assigned to a closed reservation.");
            if (BookingPaymentPolicy.IsExpiredUnconfirmed(
                    booking.Status,
                    booking.PaymentStatus,
                    booking.CreatedAt,
                    DateTime.UtcNow))
                throw new BadRequestException("Rooms cannot be assigned to an expired reservation.");
            var reason = RequireText(request.Reason, "Assignment reason", 500);
            var unit = await _db.ReservationRooms
                .FromSqlInterpolated($"SELECT * FROM reservation_rooms WHERE \"Id\" = {reservationRoomId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Reservation room not found.");
            if (unit.BookingId != booking.Id)
                throw new BadRequestException("The reservation room does not belong to this booking.");
            var advisoryKey = BitConverter.ToInt64(request.RoomId.ToByteArray(), 0);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey})",
                cancellationToken);
            var room = await _db.Rooms
                .FromSqlInterpolated($"SELECT * FROM rooms WHERE \"Id\" = {request.RoomId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Physical room not found.");
            await _db.Entry(room).Reference(item => item.RoomType).LoadAsync(cancellationToken);
            if (room.RoomTypeId != unit.RoomTypeId)
                throw new BadRequestException("The physical room does not match the reserved room type.");
            if (!room.IsOnline || room.Status is RoomStatus.Maintenance or RoomStatus.OutOfOrder)
                throw new BadRequestException("The physical room is offline or under maintenance.");
            if (booking.Status == BookingStatus.CheckedIn && room.Status != RoomStatus.Available)
                throw new BadRequestException("An in-house room move requires a clean and available destination room.");

            var checkInDate = DateOnly.FromDateTime(_hotelTime.ToHotelLocalTime(booking.CheckIn));
            var checkOutDate = DateOnly.FromDateTime(_hotelTime.ToHotelLocalTime(booking.CheckOut));
            if (await _db.RoomInventoryClosures.AnyAsync(closure =>
                    closure.IsActive &&
                    closure.RoomId == room.Id &&
                    closure.StartDate < checkOutDate &&
                    closure.EndDate > checkInDate,
                    cancellationToken))
                throw new BadRequestException("The physical room is closed for part of this stay.");
            var expirationCutoff = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
            if (await _db.ReservationRooms.AnyAsync(other =>
                    other.Id != unit.Id &&
                    other.AssignedRoomId == room.Id &&
                    other.Booking != null &&
                    other.Booking.CheckIn < booking.CheckOut &&
                    other.Booking.CheckOut > booking.CheckIn &&
                    other.Booking.Status != BookingStatus.Cancelled &&
                    other.Booking.Status != BookingStatus.CheckedOut &&
                    other.Booking.Status != BookingStatus.NoShow &&
                    !(other.Booking.Status == BookingStatus.Pending &&
                      (other.Booking.PaymentStatus == PaymentStatus.Unpaid ||
                       other.Booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                      other.Booking.CreatedAt <= expirationCutoff),
                    cancellationToken))
                throw new BadRequestException("The physical room is assigned to an overlapping reservation.");

            var previousRoomId = unit.AssignedRoomId;
            unit.AssignedRoomId = room.Id;
            unit.AssignedAtUtc = DateTime.UtcNow;
            unit.AssignedByUserId = actorId;
            if (unit.Sequence == 1) booking.RoomId = room.Id;
            if (booking.Status == BookingStatus.CheckedIn)
            {
                if (previousRoomId.HasValue && previousRoomId != room.Id)
                {
                    var previousRoom = await _db.Rooms.SingleOrDefaultAsync(
                        item => item.Id == previousRoomId.Value,
                        cancellationToken);
                    if (previousRoom is not null)
                    {
                        previousRoom.Status = RoomStatus.Dirty;
                        if (!await _db.HousekeepingTasks.AnyAsync(task =>
                                task.RoomId == previousRoom.Id &&
                                task.Status != OperationalTaskStatus.Completed &&
                                task.Status != OperationalTaskStatus.Cancelled,
                                cancellationToken))
                        {
                            _db.HousekeepingTasks.Add(new HousekeepingTask
                            {
                                Id = Guid.NewGuid(),
                                RoomId = previousRoom.Id,
                                BookingId = booking.Id,
                                Type = HousekeepingTaskType.RoomMoveCleaning,
                                Priority = WorkPriority.High,
                                Notes = $"Clean after in-house move for {booking.BookingCode}.",
                                CreatedByUserId = actorId,
                                CreatedAtUtc = DateTime.UtcNow
                            });
                        }
                    }
                }
                room.Status = RoomStatus.Occupied;
            }
            AddAudit(actorId, previousRoomId.HasValue ? "ROOM_ASSIGNMENT_MOVED" : "ROOM_ASSIGNED",
                "ReservationRoom", unit.Id,
                JsonSerializer.Serialize(new { AssignedRoomId = previousRoomId }),
                JsonSerializer.Serialize(new
                {
                    unit.BookingId,
                    unit.RoomTypeId,
                    unit.AssignedRoomId,
                    unit.AssignedAtUtc,
                    Reason = reason
                }));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReservationRoomDto(
                unit.Id,
                unit.Sequence,
                unit.RoomTypeId,
                unit.RoomTypeCode,
                unit.RoomTypeName,
                room.Id,
                room.RoomNumber,
                unit.AssignedAtUtc,
                unit.AssignedByUserId);
        });
    }

    private void ValidateStay(DateOnly checkInDate, DateOnly checkOutDate, int requestedUnits)
    {
        if (checkInDate < _hotelTime.Today || checkOutDate <= checkInDate ||
            checkOutDate.DayNumber - checkInDate.DayNumber > 90)
            throw new BadRequestException("Inventory requests require a future stay of 1 to 90 nights.");
        if (requestedUnits is < 1 or > 10)
            throw new BadRequestException("Requested inventory must be between 1 and 10 rooms.");
    }

    private async Task SaveConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("The room-type configuration conflicts with an existing code.");
        }
    }

    private async Task RequireInventoryActorAsync(
        Guid actorId,
        bool allowReservationsStaff,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The inventory actor is invalid.");
        var actor = await _db.Users.AsNoTracking().SingleOrDefaultAsync(user =>
            user.Id == actorId && user.Status == ProfileStatus.Active,
            cancellationToken);
        if (actor is null)
            throw new UnauthorizedAccessException("The inventory actor is not active.");
        var allowed = actor.Role is UserRole.Admin or UserRole.Manager ||
                      allowReservationsStaff && actor.Role == UserRole.Staff &&
                      actor.Department is "Reception" or "FrontDesk";
        if (!allowed)
            throw new UnauthorizedAccessException("The actor is not authorized for this inventory operation.");
    }

    private void AddAudit(
        Guid actorId,
        string action,
        string entityType,
        Guid entityId,
        string? oldData,
        string? newData) => _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            OldDataJson = oldData,
            NewDataJson = newData,
            CreatedAt = DateTime.UtcNow
        });

    private static RoomTypeDto ToDto(RoomType type, int roomCount) => new(
        type.Id, type.Code, type.Name, type.Category, type.BaseOccupancy,
        type.MaxOccupancy, type.BasePricePerNight, type.Description, type.Amenities, type.IsActive,
        roomCount, type.UpdatedAtUtc);

    private static InventoryClosureDto ToClosureDto(RoomInventoryClosure item) => new(
        item.Id, item.RoomTypeId, item.RoomType?.Code ?? string.Empty,
        item.RoomId, item.Room?.RoomNumber, item.StartDate, item.EndDate,
        item.Units, item.Reason, item.IsActive, item.CreatedAtUtc, item.UpdatedAtUtc);

    private static string NormalizeCode(string value)
    {
        var normalized = RequireText(value, "Room-type code", 30).ToUpperInvariant();
        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new BadRequestException("Room-type code contains unsupported characters.");
        return normalized;
    }

    private static string RequireText(string? value, string field, int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length == 0 || cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is required and cannot exceed {maximumLength} characters.");
        return cleaned;
    }

    private static string CleanText(string? value, string field, int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }

    private static List<string> NormalizeAmenities(IEnumerable<string>? amenities)
    {
        var values = amenities?.ToList() ?? [];
        if (values.Count > 50 || values.Any(item =>
                item is null || item.Length > 120 || item.Any(char.IsControl)))
            throw new BadRequestException("Room-type amenities are invalid or too long.");
        return values.Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
