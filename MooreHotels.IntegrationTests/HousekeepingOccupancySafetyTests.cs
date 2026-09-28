using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class HousekeepingOccupancySafetyTests
{
    private readonly ManualTransferTestFixture _fixture;
    public HousekeepingOccupancySafetyTests(ManualTransferTestFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(RoomStatus.Occupied)]
    [InlineData(RoomStatus.Dirty)]
    [InlineData(RoomStatus.Available)]
    [InlineData(RoomStatus.OutOfOrder)]
    public async Task Stayover_service_preserves_room_state_and_never_creates_release_inspection(RoomStatus status)
    {
        var booking = await _fixture.CreateBookingAsync(bookingStatus: BookingStatus.CheckedIn);
        var roomId = booking.RoomId!.Value;
        await _fixture.WithDbAsync(async db =>
        {
            var room = await db.Rooms.SingleAsync(item => item.Id == roomId);
            room.Status = status;
            room.IsOnline = false;
            return await db.SaveChangesAsync();
        });
        var cleaner = await _fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var task = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            roomId, booking.Id, HousekeepingTaskType.StayoverService, WorkPriority.Normal, "Refresh room supplies."),
            _fixture.Manager.Id);
        foreach (var next in new[] { OperationalTaskStatus.InProgress, OperationalTaskStatus.Completed })
        {
            await service.UpdateTaskAsync(task.Id, new UpdateHousekeepingTaskRequest(next, null, null, null), cleaner.Id);
            var state = await _fixture.WithDbAsync(db => db.Rooms.AsNoTracking().SingleAsync(item => item.Id == roomId));
            Assert.Equal(status, state.Status);
            Assert.False(state.IsOnline);
        }
        Assert.False(await _fixture.WithDbAsync(db => db.HousekeepingTasks.AnyAsync(item =>
            item.RoomId == roomId && item.Type == HousekeepingTaskType.Inspection)));
        Assert.Equal(BookingStatus.CheckedIn, await _fixture.WithDbAsync(db =>
            db.Bookings.Where(item => item.Id == booking.Id).Select(item => item.Status).SingleAsync()));
    }

    [Theory]
    [InlineData(HousekeepingTaskType.CheckoutCleaning, OperationalTaskStatus.Pending, OperationalTaskStatus.InProgress, "status")]
    [InlineData(HousekeepingTaskType.CheckoutCleaning, OperationalTaskStatus.InProgress, OperationalTaskStatus.Completed, "legacy")]
    [InlineData(HousekeepingTaskType.RoomMoveCleaning, OperationalTaskStatus.Pending, OperationalTaskStatus.InProgress, "unit")]
    [InlineData(HousekeepingTaskType.MaintenanceRecovery, OperationalTaskStatus.InProgress, OperationalTaskStatus.Completed, "unit")]
    [InlineData(HousekeepingTaskType.Inspection, OperationalTaskStatus.Pending, OperationalTaskStatus.InProgress, "legacy")]
    [InlineData(HousekeepingTaskType.Inspection, OperationalTaskStatus.InProgress, OperationalTaskStatus.Completed, "unit")]
    [InlineData(HousekeepingTaskType.Inspection, OperationalTaskStatus.InProgress, OperationalTaskStatus.Completed, "status")]
    public async Task Occupancy_blocks_turnover_and_release_without_partial_writes(
        HousekeepingTaskType type, OperationalTaskStatus initial, OperationalTaskStatus next, string occupancySource)
    {
        var booking = await _fixture.CreateBookingAsync(bookingStatus:
            occupancySource == "status" ? BookingStatus.CheckedOut : BookingStatus.CheckedIn);
        var roomId = booking.RoomId!.Value;
        var taskId = Guid.NewGuid();
        var roomStatus = occupancySource == "status" ? RoomStatus.Occupied : RoomStatus.Clean;
        await _fixture.WithDbAsync(async db =>
        {
            var room = await db.Rooms.SingleAsync(item => item.Id == roomId);
            room.Status = roomStatus;
            room.IsOnline = false;
            if (occupancySource == "unit")
                (await db.Bookings.SingleAsync(item => item.Id == booking.Id)).RoomId = null;
            if (occupancySource == "legacy")
                foreach (var unit in await db.ReservationRooms.Where(item => item.BookingId == booking.Id).ToListAsync())
                    unit.AssignedRoomId = null;
            db.HousekeepingTasks.Add(new HousekeepingTask
            {
                Id = taskId,
                RoomId = roomId,
                BookingId = booking.Id,
                Type = type,
                Status = initial,
                CreatedByUserId = _fixture.Manager.Id
            });
            return await db.SaveChangesAsync();
        });
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        await Assert.ThrowsAsync<ConflictException>(() => service.UpdateTaskAsync(taskId,
            new UpdateHousekeepingTaskRequest(next, null, true, null), _fixture.Manager.Id));
        var stored = await _fixture.WithDbAsync(db => db.HousekeepingTasks.AsNoTracking().SingleAsync(item => item.Id == taskId));
        Assert.Equal(initial, stored.Status);
        Assert.Null(stored.CompletedAtUtc);
        Assert.Null(stored.AssignedToUserId);
        var unchanged = await _fixture.WithDbAsync(db => db.Rooms.AsNoTracking().SingleAsync(item => item.Id == roomId));
        Assert.Equal(roomStatus, unchanged.Status);
        Assert.False(unchanged.IsOnline);
        Assert.False(await _fixture.WithDbAsync(db => db.AuditLogs.AnyAsync(item =>
            item.EntityId == taskId.ToString() && item.Action == "HOUSEKEEPING_TASK_UPDATED")));
    }

    [Fact]
    public async Task Inspection_cannot_release_room_with_another_unfinished_cleaning_task()
    {
        var room = await _fixture.CreateRoomAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var cleaning = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            room.Id, null, HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, null), _fixture.Manager.Id);
        var inspection = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            room.Id, null, HousekeepingTaskType.Inspection, WorkPriority.Normal, null), _fixture.Manager.Id);
        await service.UpdateTaskAsync(inspection.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, null, null, null), _fixture.Manager.Id);
        await Assert.ThrowsAsync<ConflictException>(() => service.UpdateTaskAsync(inspection.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Completed, null, true, null), _fixture.Manager.Id));
        Assert.NotEqual(RoomStatus.Available, await _fixture.WithDbAsync(db =>
            db.Rooms.Where(item => item.Id == room.Id).Select(item => item.Status).SingleAsync()));
        Assert.Equal(OperationalTaskStatus.Pending, (await service.GetTasksAsync()).Single(item => item.Id == cleaning.Id).Status);
    }

    [Fact]
    public async Task Http_contract_uses_camel_case_enums_and_returns_conflict_for_occupied_room()
    {
        var booking = await _fixture.CreateBookingAsync(bookingStatus: BookingStatus.CheckedIn);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var task = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            booking.RoomId!.Value, booking.Id, HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, null), _fixture.Manager.Id);
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/housekeeping/tasks");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.Manager.Token);
        using var response = await _fixture.Client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var wireTask = payload.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == task.Id);
        Assert.Equal("checkoutCleaning", wireTask.GetProperty("type").GetString());
        Assert.Equal("pending", wireTask.GetProperty("status").GetString());
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/housekeeping/tasks/{task.Id}");
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.Manager.Token);
        put.Content = JsonContent.Create(new { status = "inProgress" });
        using var rejected = await _fixture.Client.SendAsync(put);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task Concurrent_cleaner_claims_allow_only_one_assignee()
    {
        var room = await _fixture.CreateRoomAsync();
        var first = await _fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var second = await _fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var task = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            room.Id, null, HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, null), _fixture.Manager.Id);
        async Task<bool> ClaimAsync(Guid actorId)
        {
            await using var claimScope = _fixture.Services.CreateAsyncScope();
            var claimService = claimScope.ServiceProvider.GetRequiredService<IHousekeepingService>();
            try
            {
                await claimService.UpdateTaskAsync(task.Id,
                    new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, null, null, null), actorId);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
        }
        var results = await Task.WhenAll(ClaimAsync(first.Id), ClaimAsync(second.Id));
        Assert.Single(results, accepted => accepted);
        var stored = await _fixture.WithDbAsync(db => db.HousekeepingTasks.AsNoTracking().SingleAsync(item => item.Id == task.Id));
        Assert.Equal(OperationalTaskStatus.InProgress, stored.Status);
        Assert.Contains(stored.AssignedToUserId!.Value, new[] { first.Id, second.Id });
    }

    [Fact]
    public async Task Passed_inspection_keeps_room_offline_when_maintenance_is_open()
    {
        var room = await _fixture.CreateRoomAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var time = scope.ServiceProvider.GetRequiredService<IHotelTimeService>();
        var inspection = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            room.Id, null, HousekeepingTaskType.Inspection, WorkPriority.Normal, null), _fixture.Manager.Id);
        await service.UpdateTaskAsync(inspection.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, null, null, null), _fixture.Manager.Id);
        await service.CreateWorkOrderAsync(new CreateMaintenanceWorkOrderRequest(
            room.Id, "Repair faulty lighting", "Repair the faulty lighting before the room is released.",
            WorkPriority.High, time.Today, time.Today.AddDays(1), null), _fixture.Manager.Id);
        await service.UpdateTaskAsync(inspection.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Completed, null, true, null), _fixture.Manager.Id);
        var stored = await _fixture.WithDbAsync(db => db.Rooms.AsNoTracking().SingleAsync(item => item.Id == room.Id));
        Assert.Equal(RoomStatus.OutOfOrder, stored.Status);
        Assert.False(stored.IsOnline);
    }

    [Fact]
    public async Task Cleaning_task_creation_does_not_overwrite_checked_in_reservation_with_drifted_room_status()
    {
        var booking = await _fixture.CreateBookingAsync(bookingStatus: BookingStatus.CheckedIn);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            booking.RoomId!.Value, booking.Id, HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, null), _fixture.Manager.Id);
        Assert.Equal(RoomStatus.Available, await _fixture.WithDbAsync(db =>
            db.Rooms.Where(item => item.Id == booking.RoomId).Select(item => item.Status).SingleAsync()));
    }
}
