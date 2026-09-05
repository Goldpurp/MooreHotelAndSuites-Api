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

public sealed class OperationalReportingService : IOperationalReportingService
{
    private readonly MooreHotelsDbContext _db;
    private readonly IHotelTimeService _hotelTime;

    public OperationalReportingService(MooreHotelsDbContext db, IHotelTimeService hotelTime)
    {
        _db = db;
        _hotelTime = hotelTime;
    }

    public async Task<OperationsBoardDto> GetBoardAsync(
        DateOnly businessDate,
        CancellationToken cancellationToken = default)
    {
        if (businessDate == default) businessDate = _hotelTime.Today;
        ValidateRange(businessDate, businessDate);
        var startUtc = _hotelTime.GetLocalDayStartUtc(businessDate);
        var endUtc = _hotelTime.GetLocalDayEndUtc(businessDate);
        var bookings = await BookingQuery()
            .Where(item =>
                item.Status == BookingStatus.CheckedIn ||
                item.CheckIn >= startUtc && item.CheckIn < endUtc ||
                item.CheckOut >= startUtc && item.CheckOut < endUtc)
            .OrderBy(item => item.CheckIn).ThenBy(item => item.BookingCode)
            .ToListAsync(cancellationToken);

        var arrivals = bookings.Where(item => item.CheckIn >= startUtc && item.CheckIn < endUtc &&
            item.Status is not (BookingStatus.Cancelled or BookingStatus.NoShow)).Select(ToBoardItem).ToArray();
        var departures = bookings.Where(item => item.CheckOut >= startUtc && item.CheckOut < endUtc &&
            item.Status is not (BookingStatus.Cancelled or BookingStatus.NoShow)).Select(ToBoardItem).ToArray();
        var inHouse = bookings.Where(item => item.Status == BookingStatus.CheckedIn).Select(ToBoardItem).ToArray();
        var outstanding = bookings.Where(item => item.Folio is not null &&
            FolioAccounting.Calculate(item.Folio.Entries).Balance != 0).Select(ToBoardItem).ToArray();
        var unassigned = bookings
            .Where(item => item.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn)
            .SelectMany(booking => booking.ReservationRooms
                .Where(room => !room.AssignedRoomId.HasValue)
                .Select(room => new UnassignedReservationRoomDto(
                    booking.Id, booking.BookingCode, GuestName(booking), ToReservationRoomDto(room))))
            .ToArray();
        var housekeeping = await _db.HousekeepingTasks.AsNoTracking()
            .Include(item => item.Room).Include(item => item.Booking)
            .Where(item => item.Status != OperationalTaskStatus.Completed &&
                           item.Status != OperationalTaskStatus.Cancelled ||
                           item.CompletedAtUtc >= startUtc && item.CompletedAtUtc < endUtc)
            .OrderByDescending(item => item.Priority).ThenBy(item => item.CreatedAtUtc)
            .Take(1000).ToListAsync(cancellationToken);
        var maintenance = await _db.MaintenanceWorkOrders.AsNoTracking()
            .Include(item => item.Room)
            .Where(item => item.Status != MaintenanceWorkOrderStatus.Resolved &&
                           item.Status != MaintenanceWorkOrderStatus.Cancelled ||
                           item.ResolvedAtUtc >= startUtc && item.ResolvedAtUtc < endUtc)
            .OrderByDescending(item => item.Priority).ThenBy(item => item.CreatedAtUtc)
            .Take(1000).ToListAsync(cancellationToken);
        return new OperationsBoardDto(
            businessDate, arrivals, departures, inHouse, outstanding, unassigned,
            housekeeping.Select(HousekeepingService.ToDto).ToArray(),
            maintenance.Select(HousekeepingService.ToDto).ToArray());
    }

    public async Task<IReadOnlyList<ReservationCalendarItemDto>> GetCalendarAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(fromDate, toDate);
        var startUtc = _hotelTime.GetLocalDayStartUtc(fromDate);
        var endUtc = _hotelTime.GetLocalDayEndUtc(toDate);
        var bookings = await BookingQuery().Where(item =>
                item.CheckIn < endUtc && item.CheckOut > startUtc &&
                item.Status != BookingStatus.Cancelled && item.Status != BookingStatus.NoShow)
            .OrderBy(item => item.CheckIn).ThenBy(item => item.BookingCode)
            .Take(5000).ToListAsync(cancellationToken);
        return bookings.Select(item =>
        {
            var balance = item.Folio is null ? default : FolioAccounting.Calculate(item.Folio.Entries);
            return new ReservationCalendarItemDto(
                item.Id, item.BookingCode, GuestName(item), item.RoomTypeId,
                item.RoomType?.Name ?? string.Empty, item.RoomQuantity, item.CheckIn, item.CheckOut,
                item.Status, item.PaymentStatus, balance.AmountDue,
                item.ReservationRooms.Where(room => room.AssignedRoom is not null)
                    .OrderBy(room => room.Sequence).Select(room => room.AssignedRoom!.RoomNumber).ToArray());
        }).ToArray();
    }

    public async Task<OperationalReportDto> GetReportAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(fromDate, toDate);
        var startUtc = _hotelTime.GetLocalDayStartUtc(fromDate);
        var endUtc = _hotelTime.GetLocalDayEndUtc(toDate);
        var onlineRooms = await _db.Rooms.AsNoTracking()
            .Where(room => room.IsOnline)
            .Select(room => new { room.Id, room.Status }).ToArrayAsync(cancellationToken);
        var sellableRoomIds = onlineRooms.Select(room => room.Id).ToArray();
        var currentlyUnavailableRoomIds = onlineRooms
            .Where(room => room.Status is RoomStatus.Maintenance or RoomStatus.OutOfOrder)
            .Select(room => room.Id).ToArray();
        var closures = await _db.RoomInventoryClosures.AsNoTracking()
            .Where(item => item.IsActive && item.StartDate <= toDate && item.EndDate > fromDate)
            .Select(item => new { item.RoomId, item.StartDate, item.EndDate, item.Units })
            .ToListAsync(cancellationToken);
        var availableRoomNights = 0;
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            var active = closures.Where(item => item.StartDate <= date && item.EndDate > date).ToArray();
            var closedRoomIds = active
                .Where(item => item.RoomId.HasValue && sellableRoomIds.Contains(item.RoomId.Value))
                .Select(item => item.RoomId!.Value);
            if (date >= _hotelTime.Today)
                closedRoomIds = closedRoomIds.Concat(currentlyUnavailableRoomIds);
            var physicalClosures = closedRoomIds.Distinct().Count();
            var typeClosures = active.Where(item => !item.RoomId.HasValue).Sum(item => item.Units);
            availableRoomNights += Math.Max(0, sellableRoomIds.Length -
                Math.Min(sellableRoomIds.Length, physicalClosures + typeClosures));
        }

        var bookings = await _db.Bookings.AsNoTracking()
            .Include(item => item.Folio)!.ThenInclude(folio => folio!.Entries)
            .Include(item => item.Quote)!.ThenInclude(quote => quote!.Lines)
            .Where(item => item.CheckIn < endUtc && item.CheckOut > startUtc)
            .ToListAsync(cancellationToken);
        var stayed = bookings.Where(item => item.Status is BookingStatus.CheckedIn or BookingStatus.CheckedOut).ToArray();
        var occupiedRoomNights = 0;
        var roomRevenue = 0m;
        foreach (var booking in stayed)
        {
            var arrivalDate = DateOnly.FromDateTime(_hotelTime.ToHotelLocalTime(booking.CheckIn));
            var departureDate = DateOnly.FromDateTime(_hotelTime.ToHotelLocalTime(booking.CheckOut));
            var overlapStart = arrivalDate > fromDate ? arrivalDate : fromDate;
            var reportExclusiveEnd = toDate.AddDays(1);
            var overlapEnd = departureDate < reportExclusiveEnd ? departureDate : reportExclusiveEnd;
            var overlapNights = Math.Max(0, overlapEnd.DayNumber - overlapStart.DayNumber);
            var stayNights = Math.Max(1, departureDate.DayNumber - arrivalDate.DayNumber);
            occupiedRoomNights += overlapNights * booking.RoomQuantity;
            var netStayRoomRevenue = Math.Max(
                0m,
                booking.RoomSubtotal - booking.DiscountAmount - booking.IncludedTaxAmount);
            var quotedGross = booking.Quote?.Lines
                .Where(line => line.Type == PricingLineType.RoomNight &&
                               line.StayDate.HasValue &&
                               line.StayDate.Value >= overlapStart &&
                               line.StayDate.Value < overlapEnd)
                .Sum(line => line.Amount) ?? 0m;
            roomRevenue += quotedGross > 0m && booking.RoomSubtotal > 0m
                ? netStayRoomRevenue * quotedGross / booking.RoomSubtotal
                : netStayRoomRevenue * overlapNights / stayNights;
        }
        roomRevenue = FolioAccounting.Money(roomRevenue);

        var transactionEntries = await _db.FolioEntries.AsNoTracking()
            .Where(item => item.PostedAtUtc >= startUtc && item.PostedAtUtc < endUtc)
            .ToListAsync(cancellationToken);
        var reversedIds = transactionEntries.Where(item => item.ReversesEntryId.HasValue)
            .Select(item => item.ReversesEntryId!.Value).ToArray();
        var reversed = reversedIds.Length == 0
            ? new Dictionary<Guid, FolioEntry>()
            : await _db.FolioEntries.AsNoTracking().Where(item => reversedIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
        var payments = FolioAccounting.Money(transactionEntries.Sum(item =>
            item.Type == FolioEntryType.Payment ? item.Amount :
            item.Type == FolioEntryType.Void && item.ReversesEntryId.HasValue &&
            reversed.TryGetValue(item.ReversesEntryId.Value, out var original) &&
            original.Type == FolioEntryType.Payment ? -item.Amount : 0m));
        var refunds = FolioAccounting.Money(transactionEntries.Sum(item =>
            item.Type == FolioEntryType.Refund ? item.Amount :
            item.Type == FolioEntryType.Void && item.ReversesEntryId.HasValue &&
            reversed.TryGetValue(item.ReversesEntryId.Value, out var original) &&
            original.Type == FolioEntryType.Refund ? -item.Amount : 0m));
        var openFolios = await _db.Folios.AsNoTracking().Include(item => item.Entries)
            .Where(item => item.Status == FolioStatus.Open)
            .ToListAsync(cancellationToken);
        var balances = openFolios.Select(item => FolioAccounting.Calculate(item.Entries)).ToArray();
        var receivables = FolioAccounting.Money(balances.Sum(item => item.AmountDue));
        var guestCredits = FolioAccounting.Money(balances.Sum(item => item.GuestCredit));
        var actualCheckIns = await _db.VisitRecords.AsNoTracking().CountAsync(item =>
            item.Action == "CHECK_IN" && item.Timestamp >= startUtc && item.Timestamp < endUtc,
            cancellationToken);
        var actualCheckOuts = await _db.VisitRecords.AsNoTracking().CountAsync(item =>
            item.Action == "CHECK_OUT" && item.Timestamp >= startUtc && item.Timestamp < endUtc,
            cancellationToken);
        var scheduledArrivals = bookings.Count(item => item.CheckIn >= startUtc && item.CheckIn < endUtc &&
            item.Status is not (BookingStatus.Cancelled or BookingStatus.NoShow));
        var scheduledDepartures = bookings.Count(item => item.CheckOut >= startUtc && item.CheckOut < endUtc &&
            item.Status is not (BookingStatus.Cancelled or BookingStatus.NoShow));
        var adr = occupiedRoomNights == 0 ? 0m : FolioAccounting.Money(roomRevenue / occupiedRoomNights);
        var revPar = availableRoomNights == 0 ? 0m : FolioAccounting.Money(roomRevenue / availableRoomNights);
        return new OperationalReportDto(
            fromDate, toDate, availableRoomNights, occupiedRoomNights, roomRevenue, adr, revPar,
            payments, refunds, receivables, guestCredits, scheduledArrivals, actualCheckIns,
            scheduledDepartures, actualCheckOuts,
            await _db.Bookings.AsNoTracking().CountAsync(
                item => item.PaymentStatus == PaymentStatus.RefundPending,
                cancellationToken));
    }

    public async Task<NightAuditDto> CloseNightAuditAsync(
        DateOnly businessDate,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        if (businessDate >= _hotelTime.Today)
            throw new BadRequestException("Only a completed hotel business date can be closed.");
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var lockKey = (long)businessDate.DayNumber;
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
            var existing = await _db.NightAudits.AsNoTracking()
                .SingleOrDefaultAsync(item => item.BusinessDate == businessDate, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ToNightAuditDto(existing);
            }
            var snapshot = await GetReportAsync(businessDate, businessDate, cancellationToken);
            var closedAt = DateTime.UtcNow;
            var audit = new NightAudit
            {
                Id = Guid.NewGuid(),
                BusinessDate = businessDate,
                AvailableRoomNights = snapshot.AvailableRoomNights,
                OccupiedRoomNights = snapshot.OccupiedRoomNights,
                RoomRevenue = snapshot.RoomRevenue,
                Payments = snapshot.Payments,
                Refunds = snapshot.Refunds,
                Receivables = snapshot.Receivables,
                GuestCredits = snapshot.GuestCredits,
                Adr = snapshot.Adr,
                RevPar = snapshot.RevPar,
                SnapshotJson = JsonSerializer.Serialize(snapshot),
                ClosedAtUtc = closedAt,
                ClosedByUserId = actorId
            };
            _db.NightAudits.Add(audit);
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ProfileId = actorId,
                Action = "NIGHT_AUDIT_CLOSED",
                EntityType = "NightAudit",
                EntityId = audit.Id.ToString(),
                NewDataJson = audit.SnapshotJson,
                CreatedAt = closedAt
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new NightAuditDto(audit.Id, businessDate, snapshot, closedAt, actorId);
        });
    }

    public async Task<IReadOnlyList<NightAuditDto>> GetNightAuditsAsync(
        CancellationToken cancellationToken = default) =>
        (await _db.NightAudits.AsNoTracking().OrderByDescending(item => item.BusinessDate)
            .Take(366).ToListAsync(cancellationToken)).Select(ToNightAuditDto).ToArray();

    private IQueryable<Booking> BookingQuery() => _db.Bookings.AsNoTracking()
        .Include(item => item.Guest).Include(item => item.RoomType)
        .Include(item => item.ReservationRooms).ThenInclude(item => item.AssignedRoom)
        .Include(item => item.Folio)!.ThenInclude(item => item!.Entries);

    private static OperationsBoardItemDto ToBoardItem(Booking item)
    {
        var balance = item.Folio is null ? default : FolioAccounting.Calculate(item.Folio.Entries);
        return new OperationsBoardItemDto(
            item.Id, item.BookingCode, GuestName(item), item.RoomTypeId,
            item.RoomType?.Name ?? string.Empty, item.RoomQuantity, item.CheckIn, item.CheckOut,
            item.Status, item.PaymentStatus, balance.AmountDue, balance.GuestCredit,
            item.ReservationRooms.OrderBy(room => room.Sequence).Select(ToReservationRoomDto).ToArray());
    }

    private static ReservationRoomDto ToReservationRoomDto(ReservationRoom room) => new(
        room.Id, room.Sequence, room.RoomTypeId, room.RoomTypeCode, room.RoomTypeName,
        room.AssignedRoomId, room.AssignedRoom?.RoomNumber, room.AssignedAtUtc, room.AssignedByUserId);

    private static string GuestName(Booking booking) =>
        $"{booking.Guest?.FirstName} {booking.Guest?.LastName}".Trim();

    private static NightAuditDto ToNightAuditDto(NightAudit audit)
    {
        var snapshot = JsonSerializer.Deserialize<OperationalReportDto>(audit.SnapshotJson) ??
            new OperationalReportDto(
                audit.BusinessDate, audit.BusinessDate, audit.AvailableRoomNights,
                audit.OccupiedRoomNights, audit.RoomRevenue, audit.Adr, audit.RevPar,
                audit.Payments, audit.Refunds, audit.Receivables, audit.GuestCredits, 0, 0, 0, 0, 0);
        return new NightAuditDto(audit.Id, audit.BusinessDate, snapshot, audit.ClosedAtUtc, audit.ClosedByUserId);
    }

    private static void ValidateRange(DateOnly fromDate, DateOnly toDate)
    {
        if (fromDate == default || toDate == default ||
            toDate < fromDate || toDate.DayNumber - fromDate.DayNumber > 365)
            throw new BadRequestException("The date range must be ordered and cannot exceed 366 days.");
    }
}
