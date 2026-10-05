using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

// QA-only coverage. The fixture creates and removes its own database and disables external services.
[Collection(ManualTransferCollection.Name)]
public sealed class LiveQaCompletionMatrixTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_checkout_consumes_one_quote_and_creates_one_booking(bool concurrent)
    {
        var room = await fixture.CreateRoomAsync();
        var checkIn = DateTime.UtcNow.Date.AddDays(40);
        var checkOut = checkIn.AddDays(2);
        var quote = await Json(HttpMethod.Post, "/api/pricing/quotes", new
        {
            roomId = room.Id,
            checkIn,
            checkOut,
            adultCount = 1,
            childCount = 0
        });
        Assert.Equal(HttpStatusCode.OK, quote.Status);
        var quoteId = quote.Body.GetProperty("quoteId").GetGuid();
        var body = new
        {
            roomId = room.Id,
            checkIn,
            checkOut,
            adultCount = 1,
            childCount = 0,
            guestFirstName = "Isolated",
            guestLastName = "Repeat checkout",
            guestEmail = $"repeat-{Guid.NewGuid():N}@example.test",
            guestPhone = "+2348000000091",
            paymentMethod = "directTransfer",
            quoteId,
            quoteToken = quote.Body.GetProperty("quoteToken").GetString()
        };
        var results = concurrent
            ? await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Json(HttpMethod.Post, "/api/bookings", body)))
            : new[] { await Json(HttpMethod.Post, "/api/bookings", body), await Json(HttpMethod.Post, "/api/bookings", body) };
        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        Assert.All(results.Where(r => r.Status != HttpStatusCode.OK), r =>
            Assert.True(r.Status is HttpStatusCode.Conflict or HttpStatusCode.BadRequest,
                $"Retry should safely reject, received {(int)r.Status}."));
        var saved = await fixture.WithDbAsync(async db => new
        {
            Bookings = await db.Bookings.CountAsync(b => b.QuoteId == quoteId),
            Consumed = await db.BookingQuotes.Where(q => q.Id == quoteId).Select(q => q.ConsumedAtUtc).SingleAsync()
        });
        Assert.Equal(1, saved.Bookings);
        Assert.NotNull(saved.Consumed);
    }

    [Theory]
    [InlineData("access")]
    [InlineData("rectification")]
    [InlineData("erasure")]
    [InlineData("restriction")]
    [InlineData("portability")]
    [InlineData("objection")]
    public async Task Privacy_request_duplicate_ownership_and_status_guards(string type)
    {
        var owner = await fixture.CreateUserAsync(UserRole.Client);
        var other = await fixture.CreateUserAsync(UserRole.Client);
        var guest = await fixture.LinkGuestProfileAsync(owner);
        await fixture.LinkGuestProfileAsync(other);
        var body = new { type, details = "Isolated QA request; no real personal data." };
        var created = await Json(HttpMethod.Post, "/api/privacy/requests", body, owner);
        Assert.Equal(HttpStatusCode.Accepted, created.Status);
        var id = created.Body.GetProperty("id").GetGuid();
        Assert.Equal(guest.Id, created.Body.GetProperty("guestId").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Json(HttpMethod.Post, "/api/privacy/requests", body, owner)).Status);

        var ownList = await Json(HttpMethod.Get, "/api/privacy/requests/mine", null, owner);
        Assert.Equal(HttpStatusCode.OK, ownList.Status);
        Assert.Contains(ownList.Body.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        var otherList = await Json(HttpMethod.Get, "/api/privacy/requests/mine", null, other);
        Assert.Equal(HttpStatusCode.OK, otherList.Status);
        Assert.DoesNotContain(otherList.Body.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);

        var path = $"/api/privacy/requests/{id}/status";
        var inProgress = new { status = "inProgress", resolutionNotes = "Isolated QA review." };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Json(HttpMethod.Patch, path, inProgress)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Json(HttpMethod.Patch, path, inProgress, owner)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Json(HttpMethod.Patch, path, inProgress, fixture.Manager)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Json(HttpMethod.Patch, path,
            new { status = "pending" }, fixture.Admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Json(HttpMethod.Patch, path,
            new { status = "rejected" }, fixture.Admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Json(HttpMethod.Patch, path,
            new { status = "completed", resolutionNotes = "Missing identity/delivery evidence." }, fixture.Admin)).Status);
        var unchanged = await fixture.WithDbAsync(db => db.PrivacyRequests.AsNoTracking().SingleAsync(r => r.Id == id));
        Assert.Equal(DataSubjectRequestStatus.Pending, unchanged.Status);
        Assert.Null(unchanged.FulfilledAtUtc);

        Assert.Equal(HttpStatusCode.OK, (await Json(HttpMethod.Patch, path, inProgress, fixture.Admin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Json(HttpMethod.Patch, path,
            new { status = "rejected", resolutionNotes = "Isolated QA request closed without fulfilling or deleting data." }, fixture.Admin)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Json(HttpMethod.Patch, path, inProgress, fixture.Admin)).Status);
        var closed = await fixture.WithDbAsync(db => db.PrivacyRequests.AsNoTracking().SingleAsync(r => r.Id == id));
        Assert.Equal(DataSubjectRequestStatus.Rejected, closed.Status);
        Assert.Equal(fixture.Admin.Id, closed.ResolvedByUserId);
        Assert.NotNull(closed.ResolvedAtUtc);
        Assert.Null(closed.FulfilledAtUtc);
        Assert.Equal(HttpStatusCode.Accepted, (await Json(HttpMethod.Post, "/api/privacy/requests", body, owner)).Status);
    }

    [Fact]
    public async Task Concurrent_duplicate_privacy_requests_create_one_open_request()
    {
        var owner = await fixture.CreateUserAsync(UserRole.Client);
        var guest = await fixture.LinkGuestProfileAsync(owner);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Json(HttpMethod.Post, "/api/privacy/requests", new { type = "access", details = "Isolated concurrent QA request." }, owner)));
        var count = await fixture.WithDbAsync(db => db.PrivacyRequests.CountAsync(r =>
            r.GuestId == guest.Id && r.Type == DataSubjectRequestType.Access && r.Status == DataSubjectRequestStatus.Pending));
        Assert.True(count == 1, $"Expected one open request; found {count}. Accepted responses: {results.Count(r => r.Status == HttpStatusCode.Accepted)}.");
        Assert.Single(results, r => r.Status == HttpStatusCode.Accepted);
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Accepted), r => Assert.Equal(HttpStatusCode.Conflict, r.Status));
    }

    [Theory]
    [InlineData("notAType")]
    [InlineData("999")]
    public async Task Privacy_request_invalid_type_is_rejected_without_record(string type)
    {
        var owner = await fixture.CreateUserAsync(UserRole.Client);
        var guest = await fixture.LinkGuestProfileAsync(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await Json(HttpMethod.Post, "/api/privacy/requests", new { type }, owner)).Status);
        Assert.Equal(0, await fixture.WithDbAsync(db => db.PrivacyRequests.CountAsync(r => r.GuestId == guest.Id)));
    }

    [Fact]
    public async Task Housekeeping_reassignment_revokes_previous_workers_control()
    {
        var first = await fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var second = await fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var room = await fixture.CreateRoomAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var task = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            room.Id, null, HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, "Isolated reassignment QA"), fixture.Manager.Id);
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            room.Id, null, HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, "Duplicate QA"), fixture.Manager.Id));
        await service.UpdateTaskAsync(task.Id, new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Assigned, first.Id, null, "Assign first"), fixture.Manager.Id);
        var reassigned = await service.UpdateTaskAsync(task.Id, new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Assigned, second.Id, null, "Reassign second"), fixture.Manager.Id);
        Assert.Equal(second.Id, reassigned.AssignedToUserId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateTaskAsync(task.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, first.Id, null, "Stale first worker"), first.Id));
        var started = await service.UpdateTaskAsync(task.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, second.Id, null, "Correct worker"), second.Id);
        Assert.Equal(OperationalTaskStatus.InProgress, started.Status);
        Assert.Equal(second.Id, started.AssignedToUserId);
        var stored = await fixture.WithDbAsync(db => db.HousekeepingTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id));
        Assert.Equal("Correct worker", stored.Notes);
    }

    [Theory]
    [InlineData(OperationalTaskStatus.Pending)]
    [InlineData(OperationalTaskStatus.Assigned)]
    [InlineData(OperationalTaskStatus.InProgress)]
    public async Task Cancelling_task_is_terminal_and_does_not_release_unclean_room(OperationalTaskStatus initial)
    {
        var cleaner = await fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var room = await fixture.CreateRoomAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IHousekeepingService>();
        var task = await service.CreateTaskAsync(new CreateHousekeepingTaskRequest(
            room.Id, null, HousekeepingTaskType.CheckoutCleaning, WorkPriority.Normal, "Isolated cancellation QA"), fixture.Manager.Id);
        if (initial != OperationalTaskStatus.Pending)
            await service.UpdateTaskAsync(task.Id, new UpdateHousekeepingTaskRequest(initial, cleaner.Id, null, "Prepare QA state"), fixture.Manager.Id);
        await service.UpdateTaskAsync(task.Id, new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Cancelled, null, null, "QA cancellation reason"), fixture.Manager.Id);
        await service.UpdateTaskAsync(task.Id, new UpdateHousekeepingTaskRequest(OperationalTaskStatus.Cancelled, null, null, "Duplicate request"), fixture.Manager.Id);
        await Assert.ThrowsAsync<BadRequestException>(() => service.UpdateTaskAsync(task.Id,
            new UpdateHousekeepingTaskRequest(OperationalTaskStatus.InProgress, cleaner.Id, null, "Attempt reopen"), fixture.Manager.Id));
        var stored = await fixture.WithDbAsync(async db => new
        {
            Task = await db.HousekeepingTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id),
            Room = await db.Rooms.AsNoTracking().SingleAsync(r => r.Id == room.Id),
            Inspections = await db.HousekeepingTasks.CountAsync(t => t.RoomId == room.Id && t.Type == HousekeepingTaskType.Inspection)
        });
        Assert.Equal(OperationalTaskStatus.Cancelled, stored.Task.Status);
        Assert.Equal("QA cancellation reason", stored.Task.Notes);
        Assert.NotEqual(RoomStatus.Available, stored.Room.Status);
        Assert.Equal(0, stored.Inspections);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Json(HttpMethod method, string path, object? body, TestUser? actor = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Moore-App-Environment", "local");
        if (actor is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await fixture.Client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content)) return (response.StatusCode, default);
        using var json = JsonDocument.Parse(content);
        return (response.StatusCode, json.RootElement.Clone());
    }
}
