using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class ManualCleaningHandoffTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task Concurrent_room_edits_create_one_task_and_completed_cleaning_requires_inspection()
    {
        var room = await fixture.CreateRoomAsync();
        var responses = await Task.WhenAll(Edit(room.Id), Edit(room.Id));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        foreach (var response in responses) response.Dispose();
        var task = await fixture.WithDbAsync(db => db.HousekeepingTasks.AsNoTracking().SingleAsync(t => t.RoomId == room.Id));
        Assert.Equal(HousekeepingTaskType.GeneralCleaning, task.Type);
        Assert.Equal(fixture.Manager.Id, task.CreatedByUserId);
        await using var scope = fixture.Services.CreateAsyncScope();
        var housekeeping = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        await housekeeping.UpdateTaskAsync(task.Id, new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, null, null, null), fixture.Manager.Id);
        await housekeeping.UpdateTaskAsync(task.Id, new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Completed, null, null, null), fixture.Manager.Id);
        Assert.Equal(RoomStatus.Clean, await fixture.WithDbAsync(db => db.Rooms.Where(r => r.Id == room.Id).Select(r => r.Status).SingleAsync()));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.HousekeepingTasks.CountAsync(t => t.RoomId == room.Id && t.Type == HousekeepingTaskType.Inspection)));
    }

    [Fact]
    public async Task Room_edit_reuses_an_existing_checkout_cleaning_task()
    {
        var room = await fixture.CreateRoomAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IHousekeepingService>().CreateTaskAsync(
            new CreateHousekeepingTaskRequest(room.Id, null, HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, "Existing cleaning"), fixture.Manager.Id);
        using var response = await Edit(room.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.HousekeepingTasks.CountAsync(t => t.RoomId == room.Id)));
    }

    [Fact]
    public async Task Manual_cleaning_cannot_overwrite_an_in_house_room()
    {
        var booking = await fixture.CreateBookingAsync(bookingStatus: BookingStatus.CheckedIn);
        var before = await fixture.WithDbAsync(db => db.Rooms.Where(r => r.Id == booking.RoomId).Select(r => r.Status).SingleAsync());
        using var response = await Edit(booking.RoomId!.Value);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await fixture.WithDbAsync(db => db.Rooms.Where(r => r.Id == booking.RoomId).Select(r => r.Status).SingleAsync()));
        Assert.False(await fixture.WithDbAsync(db => db.HousekeepingTasks.AnyAsync(t => t.RoomId == booking.RoomId)));
    }

    private async Task<HttpResponseMessage> Edit(Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/rooms/{id}");
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("Cleaning"), "Status");
        request.Content = form;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Manager.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return await fixture.Client.SendAsync(request);
    }
}
