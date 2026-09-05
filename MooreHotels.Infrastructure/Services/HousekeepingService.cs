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

public sealed class HousekeepingService : IHousekeepingService
{
    private readonly MooreHotelsDbContext _db;
    private readonly IHotelTimeService _hotelTime;

    public HousekeepingService(MooreHotelsDbContext db, IHotelTimeService hotelTime)
    {
        _db = db;
        _hotelTime = hotelTime;
    }

    public async Task<IReadOnlyList<HousekeepingTaskDto>> GetTasksAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = await TaskQuery().AsNoTracking()
            .OrderBy(task => task.Status == OperationalTaskStatus.Completed)
            .ThenByDescending(task => task.Priority)
            .ThenBy(task => task.CreatedAtUtc)
            .Take(1000)
            .ToListAsync(cancellationToken);
        return tasks.Select(ToDto).ToArray();
    }

    public async Task<HousekeepingTaskDto> CreateTaskAsync(
        CreateHousekeepingTaskRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var room = await _db.Rooms.SingleOrDefaultAsync(item => item.Id == request.RoomId, cancellationToken)
                   ?? throw new NotFoundException("Room not found.");
        if (request.BookingId.HasValue && !await _db.Bookings.AnyAsync(
                booking => booking.Id == request.BookingId,
                cancellationToken))
            throw new NotFoundException("Booking not found.");
        if (await _db.HousekeepingTasks.AnyAsync(task =>
                task.RoomId == room.Id &&
                task.Type == request.Type &&
                task.Status != OperationalTaskStatus.Completed &&
                task.Status != OperationalTaskStatus.Cancelled,
                cancellationToken))
            throw new ConflictException("An active task of this type already exists for the room.");

        var task = NewTask(
            room.Id,
            request.BookingId,
            request.Type,
            request.Priority,
            Clean(request.Notes),
            actorId,
            DateTime.UtcNow);
        _db.HousekeepingTasks.Add(task);
        if (request.Type != HousekeepingTaskType.Inspection && room.Status != RoomStatus.Occupied)
            room.Status = RoomStatus.Dirty;
        AddAudit(actorId, "HOUSEKEEPING_TASK_CREATED", "HousekeepingTask", task.Id, new
        {
            task.RoomId,
            task.BookingId,
            task.Type,
            task.Priority
        });
        await _db.SaveChangesAsync(cancellationToken);
        task.Room = room;
        return ToDto(task);
    }

    public async Task<HousekeepingTaskDto> UpdateTaskAsync(
        Guid id,
        UpdateHousekeepingTaskRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var task = await _db.HousekeepingTasks
                .FromSqlInterpolated($"SELECT * FROM housekeeping_tasks WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Housekeeping task not found.");
            var room = await _db.Rooms.SingleAsync(item => item.Id == task.RoomId, cancellationToken);
            var oldStatus = task.Status;
            ValidateTaskTransition(task.Status, request.Status);
            if (task.Status == request.Status &&
                task.Status is OperationalTaskStatus.Completed or OperationalTaskStatus.Cancelled)
            {
                await transaction.CommitAsync(cancellationToken);
                task.Room = room;
                task.Booking = task.BookingId.HasValue
                    ? await _db.Bookings.AsNoTracking().SingleOrDefaultAsync(
                        booking => booking.Id == task.BookingId,
                        cancellationToken)
                    : null;
                return ToDto(task);
            }
            if (request.AssignedToUserId.HasValue)
            {
                if (request.Status == OperationalTaskStatus.Pending)
                    throw new BadRequestException("An assigned task must use Assigned or InProgress status.");
                await RequireActiveUserAsync(request.AssignedToUserId.Value, cancellationToken);
                task.AssignedToUserId = request.AssignedToUserId;
                task.AssignedAtUtc ??= DateTime.UtcNow;
            }
            if (request.Status == OperationalTaskStatus.Assigned && !task.AssignedToUserId.HasValue)
                throw new BadRequestException("Assign the task to an active user first.");
            if (request.Status == OperationalTaskStatus.InProgress)
            {
                task.AssignedToUserId ??= actorId;
                task.AssignedAtUtc ??= DateTime.UtcNow;
                task.StartedAtUtc ??= DateTime.UtcNow;
                room.Status = task.Type == HousekeepingTaskType.Inspection
                    ? RoomStatus.Inspected
                    : RoomStatus.Cleaning;
            }
            if (request.Status == OperationalTaskStatus.Completed)
            {
                task.CompletedAtUtc = DateTime.UtcNow;
                task.CompletedByUserId = actorId;
                if (task.Type == HousekeepingTaskType.Inspection)
                {
                    if (!request.InspectionPassed.HasValue)
                        throw new BadRequestException("Inspection completion requires a pass/fail result.");
                    task.InspectionPassed = request.InspectionPassed;
                    if (request.InspectionPassed.Value)
                    {
                        var maintenanceOpen = await _db.MaintenanceWorkOrders.AnyAsync(order =>
                            order.RoomId == room.Id &&
                            order.Status != MaintenanceWorkOrderStatus.Resolved &&
                            order.Status != MaintenanceWorkOrderStatus.Cancelled,
                            cancellationToken);
                        room.Status = maintenanceOpen ? RoomStatus.OutOfOrder : RoomStatus.Available;
                    }
                    else
                    {
                        room.Status = RoomStatus.Dirty;
                        _db.HousekeepingTasks.Add(NewTask(
                            room.Id,
                            task.BookingId,
                            HousekeepingTaskType.CheckoutCleaning,
                            WorkPriority.High,
                            "Inspection failed; corrective cleaning required.",
                            actorId,
                            DateTime.UtcNow));
                    }
                }
                else
                {
                    room.Status = RoomStatus.Clean;
                    if (!await _db.HousekeepingTasks.AnyAsync(existing =>
                            existing.RoomId == room.Id &&
                            existing.Type == HousekeepingTaskType.Inspection &&
                            existing.Status != OperationalTaskStatus.Completed &&
                            existing.Status != OperationalTaskStatus.Cancelled,
                            cancellationToken))
                    {
                        _db.HousekeepingTasks.Add(NewTask(
                            room.Id,
                            task.BookingId,
                            HousekeepingTaskType.Inspection,
                            task.Priority,
                            "Inspect and release the cleaned room.",
                            actorId,
                            DateTime.UtcNow));
                    }
                }
            }
            task.Status = request.Status;
            if (!string.IsNullOrWhiteSpace(request.Notes)) task.Notes = request.Notes.Trim();
            AddAudit(actorId, "HOUSEKEEPING_TASK_UPDATED", "HousekeepingTask", task.Id, new
            {
                OldStatus = oldStatus,
                task.Status,
                task.AssignedToUserId,
                task.InspectionPassed
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            task.Room = room;
            task.Booking = task.BookingId.HasValue
                ? await _db.Bookings.AsNoTracking().SingleOrDefaultAsync(
                    booking => booking.Id == task.BookingId,
                    cancellationToken)
                : null;
            return ToDto(task);
        });
    }

    public async Task<IReadOnlyList<MaintenanceWorkOrderDto>> GetWorkOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var orders = await WorkOrderQuery().AsNoTracking()
            .OrderBy(order => order.Status == MaintenanceWorkOrderStatus.Resolved ||
                              order.Status == MaintenanceWorkOrderStatus.Cancelled)
            .ThenByDescending(order => order.Priority)
            .ThenBy(order => order.CreatedAtUtc)
            .Take(1000)
            .ToListAsync(cancellationToken);
        return orders.Select(ToDto).ToArray();
    }

    public async Task<MaintenanceWorkOrderDto> CreateWorkOrderAsync(
        CreateMaintenanceWorkOrderRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        if (request.OutOfOrderUntil <= request.OutOfOrderFrom ||
            request.OutOfOrderFrom < _hotelTime.Today.AddDays(-1) ||
            request.OutOfOrderUntil > _hotelTime.Today.AddYears(2).AddDays(1))
            throw new BadRequestException("The out-of-order window is invalid or outside the two-year horizon.");
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var room = await _db.Rooms
                .FromSqlInterpolated($"SELECT * FROM rooms WHERE \"Id\" = {request.RoomId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Room not found.");
            var advisoryKey = BitConverter.ToInt64(room.RoomTypeId.ToByteArray(), 0);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey})", cancellationToken);
            if (await _db.MaintenanceWorkOrders.AnyAsync(order =>
                    order.RoomId == room.Id &&
                    order.Status != MaintenanceWorkOrderStatus.Resolved &&
                    order.Status != MaintenanceWorkOrderStatus.Cancelled,
                    cancellationToken))
                throw new ConflictException("An active maintenance work order already exists for this room.");
            await EnsureMaintenanceCapacityAsync(room, request, cancellationToken);
            if (request.AssignedToUserId.HasValue)
                await RequireActiveUserAsync(request.AssignedToUserId.Value, cancellationToken);
            var now = DateTime.UtcNow;
            var closure = new RoomInventoryClosure
            {
                Id = Guid.NewGuid(),
                RoomTypeId = room.RoomTypeId,
                RoomId = room.Id,
                StartDate = request.OutOfOrderFrom,
                EndDate = request.OutOfOrderUntil,
                Units = 1,
                Reason = $"Maintenance: {RequireText(request.Title, "Work-order title", 160)}",
                CreatedByUserId = actorId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var order = new MaintenanceWorkOrder
            {
                Id = Guid.NewGuid(),
                RoomId = room.Id,
                InventoryClosureId = closure.Id,
                Title = request.Title.Trim(),
                Description = RequireText(request.Description, "Work-order description", 2000),
                Priority = request.Priority,
                Status = request.AssignedToUserId.HasValue
                    ? MaintenanceWorkOrderStatus.Assigned
                    : MaintenanceWorkOrderStatus.Open,
                OutOfOrderFrom = request.OutOfOrderFrom,
                OutOfOrderUntil = request.OutOfOrderUntil,
                CreatedByUserId = actorId,
                AssignedToUserId = request.AssignedToUserId,
                CreatedAtUtc = now
            };
            _db.RoomInventoryClosures.Add(closure);
            _db.MaintenanceWorkOrders.Add(order);
            if (request.OutOfOrderFrom <= _hotelTime.Today && request.OutOfOrderUntil > _hotelTime.Today)
                room.Status = RoomStatus.OutOfOrder;
            AddAudit(actorId, "MAINTENANCE_WORK_ORDER_CREATED", "MaintenanceWorkOrder", order.Id, new
            {
                order.RoomId,
                order.Title,
                order.Priority,
                order.OutOfOrderFrom,
                order.OutOfOrderUntil,
                order.InventoryClosureId
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            order.Room = room;
            return ToDto(order);
        });
    }

    public async Task<MaintenanceWorkOrderDto> UpdateWorkOrderAsync(
        Guid id,
        UpdateMaintenanceWorkOrderRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var order = await _db.MaintenanceWorkOrders
                .FromSqlInterpolated($"SELECT * FROM maintenance_work_orders WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Maintenance work order not found.");
            ValidateWorkOrderTransition(order.Status, request.Status);
            var room = await _db.Rooms.SingleAsync(item => item.Id == order.RoomId, cancellationToken);
            if (request.AssignedToUserId.HasValue)
            {
                if (request.Status == MaintenanceWorkOrderStatus.Open)
                    throw new BadRequestException("An assigned work order must use Assigned or InProgress status.");
                await RequireActiveUserAsync(request.AssignedToUserId.Value, cancellationToken);
                order.AssignedToUserId = request.AssignedToUserId;
            }
            if (request.Status is MaintenanceWorkOrderStatus.Assigned or MaintenanceWorkOrderStatus.InProgress &&
                !order.AssignedToUserId.HasValue)
                throw new BadRequestException("Assign the work order to an active user first.");
            if (request.Status == MaintenanceWorkOrderStatus.InProgress)
            {
                order.StartedAtUtc ??= DateTime.UtcNow;
                room.Status = RoomStatus.OutOfOrder;
            }
            if (request.Status is MaintenanceWorkOrderStatus.Resolved or MaintenanceWorkOrderStatus.Cancelled)
            {
                if (request.Status == MaintenanceWorkOrderStatus.Resolved &&
                    string.IsNullOrWhiteSpace(request.ResolutionNotes))
                    throw new BadRequestException("Resolution notes are required to resolve a work order.");
                order.ResolvedAtUtc = DateTime.UtcNow;
                order.ResolvedByUserId = actorId;
                order.ResolutionNotes = Clean(request.ResolutionNotes);
                if (order.InventoryClosureId.HasValue)
                {
                    var closure = await _db.RoomInventoryClosures.SingleAsync(
                        item => item.Id == order.InventoryClosureId,
                        cancellationToken);
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
                }
                var needsRecovery = room.Status == RoomStatus.OutOfOrder ||
                                    request.Status == MaintenanceWorkOrderStatus.Resolved;
                if (needsRecovery) room.Status = RoomStatus.Dirty;
                if (needsRecovery && !await _db.HousekeepingTasks.AnyAsync(task =>
                        task.RoomId == room.Id &&
                        task.Status != OperationalTaskStatus.Completed &&
                        task.Status != OperationalTaskStatus.Cancelled,
                        cancellationToken))
                {
                    _db.HousekeepingTasks.Add(NewTask(
                        room.Id,
                        null,
                        HousekeepingTaskType.MaintenanceRecovery,
                        WorkPriority.High,
                        "Clean and inspect after maintenance release.",
                        actorId,
                        DateTime.UtcNow));
                }
            }
            order.Status = request.Status;
            AddAudit(actorId, "MAINTENANCE_WORK_ORDER_UPDATED", "MaintenanceWorkOrder", order.Id, new
            {
                order.Status,
                order.AssignedToUserId,
                order.ResolvedAtUtc
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            order.Room = room;
            return ToDto(order);
        });
    }

    public async Task CreateCheckoutTasksAsync(
        Booking booking,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        foreach (var unit in booking.ReservationRooms.Where(item => item.AssignedRoomId.HasValue))
        {
            if (await _db.HousekeepingTasks.AnyAsync(task =>
                    task.BookingId == booking.Id &&
                    task.RoomId == unit.AssignedRoomId &&
                    task.Type == HousekeepingTaskType.CheckoutCleaning,
                    cancellationToken))
                continue;
            _db.HousekeepingTasks.Add(NewTask(
                unit.AssignedRoomId!.Value,
                booking.Id,
                HousekeepingTaskType.CheckoutCleaning,
                WorkPriority.High,
                $"Checkout clean for {booking.BookingCode}.",
                actorId,
                DateTime.UtcNow));
        }
    }

    private IQueryable<HousekeepingTask> TaskQuery() => _db.HousekeepingTasks
        .Include(task => task.Room)
        .Include(task => task.Booking);

    private IQueryable<MaintenanceWorkOrder> WorkOrderQuery() => _db.MaintenanceWorkOrders
        .Include(order => order.Room);

    private async Task RequireActiveUserAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await _db.Users.AnyAsync(user => user.Id == id && user.Status == ProfileStatus.Active, cancellationToken))
            throw new BadRequestException("The assigned user is not active.");
    }

    private async Task EnsureMaintenanceCapacityAsync(
        Room room,
        CreateMaintenanceWorkOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (await _db.RoomInventoryClosures.AnyAsync(closure =>
                closure.RoomId == room.Id && closure.IsActive &&
                closure.StartDate < request.OutOfOrderUntil &&
                closure.EndDate > request.OutOfOrderFrom,
                cancellationToken))
            throw new ConflictException("The room already has an overlapping inventory closure.");

        var startUtc = _hotelTime.GetCheckInUtc(request.OutOfOrderFrom.ToDateTime(TimeOnly.MinValue));
        var endUtc = _hotelTime.GetCheckOutUtc(request.OutOfOrderUntil.ToDateTime(TimeOnly.MinValue));
        var expirationCutoff = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
        var assignedConflict = await _db.ReservationRooms.AnyAsync(unit =>
            unit.AssignedRoomId == room.Id && unit.Booking != null &&
            unit.Booking.CheckIn < endUtc && unit.Booking.CheckOut > startUtc &&
            unit.Booking.Status != BookingStatus.Cancelled &&
            unit.Booking.Status != BookingStatus.CheckedOut &&
            unit.Booking.Status != BookingStatus.NoShow &&
            !(unit.Booking.Status == BookingStatus.Pending &&
              (unit.Booking.PaymentStatus == PaymentStatus.Unpaid ||
               unit.Booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
              unit.Booking.CreatedAt <= expirationCutoff),
            cancellationToken);
        if (assignedConflict)
            throw new ConflictException("Move the overlapping reservation to another room before opening this work order.");

        var physicalRoomIds = await _db.Rooms.Where(item =>
                item.RoomTypeId == room.RoomTypeId && item.IsOnline &&
                item.Status != RoomStatus.Maintenance && item.Status != RoomStatus.OutOfOrder)
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        var reservations = await _db.ReservationRooms.Where(unit =>
                unit.RoomTypeId == room.RoomTypeId && unit.Booking != null &&
                unit.Booking.CheckIn < endUtc && unit.Booking.CheckOut > startUtc &&
                unit.Booking.Status != BookingStatus.Cancelled &&
                unit.Booking.Status != BookingStatus.CheckedOut &&
                unit.Booking.Status != BookingStatus.NoShow &&
                !(unit.Booking.Status == BookingStatus.Pending &&
                  (unit.Booking.PaymentStatus == PaymentStatus.Unpaid ||
                   unit.Booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                  unit.Booking.CreatedAt <= expirationCutoff))
            .Select(unit => new { unit.Booking!.CheckIn, unit.Booking.CheckOut })
            .ToListAsync(cancellationToken);
        var closures = await _db.RoomInventoryClosures.Where(closure =>
                closure.RoomTypeId == room.RoomTypeId && closure.IsActive &&
                closure.StartDate < request.OutOfOrderUntil && closure.EndDate > request.OutOfOrderFrom)
            .Select(closure => new { closure.RoomId, closure.StartDate, closure.EndDate, closure.Units })
            .ToListAsync(cancellationToken);
        for (var date = request.OutOfOrderFrom; date < request.OutOfOrderUntil; date = date.AddDays(1))
        {
            var dayStart = _hotelTime.GetCheckInUtc(date.ToDateTime(TimeOnly.MinValue));
            var dayEnd = _hotelTime.GetCheckOutUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue));
            var reserved = reservations.Count(stay => stay.CheckIn < dayEnd && stay.CheckOut > dayStart);
            var active = closures.Where(item => item.StartDate <= date && item.EndDate > date).ToArray();
            var closedRoomIds = active.Where(item => item.RoomId.HasValue && physicalRoomIds.Contains(item.RoomId.Value))
                .Select(item => item.RoomId!.Value);
            if (physicalRoomIds.Contains(room.Id)) closedRoomIds = closedRoomIds.Append(room.Id);
            var closedRooms = closedRoomIds.Distinct().Count();
            var closedUnits = active.Where(item => !item.RoomId.HasValue).Sum(item => item.Units);
            var usable = Math.Max(0, physicalRoomIds.Length -
                Math.Min(physicalRoomIds.Length, closedRooms + closedUnits));
            if (reserved > usable)
                throw new ConflictException("The maintenance window would overbook this room type. Move or amend reservations first.");
        }
    }

    private static void ValidateTaskTransition(OperationalTaskStatus current, OperationalTaskStatus requested)
    {
        if (current == requested) return;
        var allowed = current switch
        {
            OperationalTaskStatus.Pending => requested is OperationalTaskStatus.Assigned or
                OperationalTaskStatus.InProgress or OperationalTaskStatus.Cancelled,
            OperationalTaskStatus.Assigned => requested is OperationalTaskStatus.InProgress or
                OperationalTaskStatus.Cancelled,
            OperationalTaskStatus.InProgress => requested is OperationalTaskStatus.Completed or
                OperationalTaskStatus.Cancelled,
            _ => false
        };
        if (!allowed) throw new BadRequestException($"Cannot move a task from {current} to {requested}.");
    }

    private static void ValidateWorkOrderTransition(
        MaintenanceWorkOrderStatus current,
        MaintenanceWorkOrderStatus requested)
    {
        if (current == requested) return;
        var allowed = current switch
        {
            MaintenanceWorkOrderStatus.Open => requested is MaintenanceWorkOrderStatus.Assigned or
                MaintenanceWorkOrderStatus.InProgress or MaintenanceWorkOrderStatus.Cancelled,
            MaintenanceWorkOrderStatus.Assigned => requested is MaintenanceWorkOrderStatus.InProgress or
                MaintenanceWorkOrderStatus.Resolved or MaintenanceWorkOrderStatus.Cancelled,
            MaintenanceWorkOrderStatus.InProgress => requested is MaintenanceWorkOrderStatus.Resolved or
                MaintenanceWorkOrderStatus.Cancelled,
            _ => false
        };
        if (!allowed) throw new BadRequestException($"Cannot move a work order from {current} to {requested}.");
    }

    private static HousekeepingTask NewTask(
        Guid roomId,
        Guid? bookingId,
        HousekeepingTaskType type,
        WorkPriority priority,
        string? notes,
        Guid actorId,
        DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            BookingId = bookingId,
            Type = type,
            Priority = priority,
            Notes = notes ?? string.Empty,
            CreatedByUserId = actorId,
            CreatedAtUtc = now
        };

    private void AddAudit(Guid actorId, string action, string entityType, Guid id, object data) =>
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = id.ToString(),
            NewDataJson = JsonSerializer.Serialize(data),
            CreatedAt = DateTime.UtcNow
        });

    public static HousekeepingTaskDto ToDto(HousekeepingTask task) => new(
        task.Id,
        task.RoomId,
        task.Room?.RoomNumber ?? string.Empty,
        task.BookingId,
        task.Booking?.BookingCode,
        task.Type,
        task.Status,
        task.Priority,
        task.Notes,
        task.AssignedToUserId,
        task.CreatedAtUtc,
        task.StartedAtUtc,
        task.CompletedAtUtc,
        task.InspectionPassed);

    public static MaintenanceWorkOrderDto ToDto(MaintenanceWorkOrder order) => new(
        order.Id,
        order.RoomId,
        order.Room?.RoomNumber ?? string.Empty,
        order.InventoryClosureId,
        order.Title,
        order.Description,
        order.Priority,
        order.Status,
        order.OutOfOrderFrom,
        order.OutOfOrderUntil,
        order.AssignedToUserId,
        order.CreatedAtUtc,
        order.StartedAtUtc,
        order.ResolvedAtUtc,
        order.ResolutionNotes);

    private static string RequireText(string? value, string field, int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length == 0 || cleaned.Length > maximumLength)
            throw new BadRequestException($"{field} is required and cannot exceed {maximumLength} characters.");
        return cleaned;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
