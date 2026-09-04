using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class ProductionHardeningTests
{
    private readonly ManualTransferTestFixture _fixture;

    public ProductionHardeningTests(ManualTransferTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Broadcast_notification_read_state_is_private_to_each_staff_user()
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "Receipt isolation test",
            Message = "This broadcast must retain independent read state.",
            CreatedAt = DateTime.UtcNow
        };
        await _fixture.WithDbAsync(async db =>
        {
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();
            return true;
        });

        using (var markRead = Authorized(
                   HttpMethod.Patch,
                   $"/api/notifications/{notification.Id}/read",
                   _fixture.Admin))
        {
            var marked = await _fixture.Client.SendAsync(markRead);
            Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);
        }

        var adminNotifications = await GetStaffNotificationsAsync(_fixture.Admin);
        var managerNotifications = await GetStaffNotificationsAsync(_fixture.Manager);

        Assert.True(adminNotifications.Single(item => item.Id == notification.Id).IsRead);
        Assert.False(managerNotifications.Single(item => item.Id == notification.Id).IsRead);
        var receiptCount = await _fixture.WithDbAsync(db => db.NotificationReceipts
            .CountAsync(receipt => receipt.NotificationId == notification.Id));
        Assert.Equal(1, receiptCount);
    }

    [Fact]
    public async Task Image_delete_rejects_an_unregistered_provider_identifier()
    {
        using var request = Authorized(
            HttpMethod.Delete,
            "/api/images/delete?publicId=unregistered%2Fasset",
            _fixture.Admin);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Archived_paystack_method_is_rejected_by_the_http_validation_pipeline()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            "/api/bookings",
            new
            {
                roomId = Guid.NewGuid(),
                guestFirstName = "Archive",
                guestLastName = "Tester",
                guestEmail = "archive@example.test",
                guestPhone = "+2348012345678",
                checkIn = DateTime.UtcNow.Date.AddDays(4),
                checkOut = DateTime.UtcNow.Date.AddDays(6),
                adultCount = 1,
                childCount = 0,
                paymentMethod = "paystack"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Paystack is not supported", body, StringComparison.Ordinal);
    }

    private async Task<List<NotificationDto>> GetStaffNotificationsAsync(TestUser actor)
    {
        using var request = Authorized(HttpMethod.Get, "/api/notifications/staff", actor);
        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<List<NotificationDto>>() ?? [];
    }

    private static HttpRequestMessage Authorized(
        HttpMethod method,
        string path,
        TestUser actor)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }
}
