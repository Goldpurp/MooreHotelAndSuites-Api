using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Hubs;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Controllers;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class HotelHardeningSetOneToSixTests
{
    private readonly ManualTransferTestFixture _fixture;

    public HotelHardeningSetOneToSixTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Booking_occupancy_is_enforced_and_persisted()
    {
        var room = await _fixture.CreateRoomAsync();
        using (var excessive = PublicJson(
                   HttpMethod.Post,
                   "/api/bookings",
                   BookingBody(room.Id, 2, 1)))
        using (var response = await _fixture.Client.SendAsync(excessive))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("maximum of 2 guests", await response.Content.ReadAsStringAsync());
        }

        using var valid = PublicJson(
            HttpMethod.Post,
            "/api/bookings",
            BookingBody(room.Id, 1, 1));
        using var validResponse = await _fixture.Client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        using var payload = JsonDocument.Parse(await validResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, payload.RootElement.GetProperty("adultCount").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("childCount").GetInt32());

        var bookingId = payload.RootElement.GetProperty("id").GetGuid();
        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .Where(item => item.Id == bookingId)
            .Select(item => new { item.AdultCount, item.ChildCount })
            .SingleAsync());
        Assert.Equal(1, stored.AdultCount);
        Assert.Equal(1, stored.ChildCount);
    }

    [Fact]
    public async Task Database_trigger_rejects_occupancy_above_room_capacity()
    {
        var booking = await _fixture.CreateBookingAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithDbAsync(async db =>
        {
            var stored = await db.Bookings.SingleAsync(item => item.Id == booking.Id);
            stored.AdultCount = 3;
            await db.SaveChangesAsync();
            return true;
        }));
    }

    [Fact]
    public async Task Current_policy_acceptance_is_required_when_the_control_is_enabled()
    {
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        var previous = configuration["Privacy:RequirePolicyAcceptance"];
        configuration["Privacy:RequirePolicyAcceptance"] = "true";
        var room = await _fixture.CreateRoomAsync();

        try
        {
            using (var missingBookingAcceptance = PublicJson(
                       HttpMethod.Post,
                       "/api/bookings",
                       BookingBody(room.Id, 1, 0)))
            using (var response = await _fixture.Client.SendAsync(missingBookingAcceptance))
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using (var acceptedBooking = PublicJson(
                       HttpMethod.Post,
                       "/api/bookings",
                       new
                       {
                           roomId = room.Id,
                           guestFirstName = "Policy",
                           guestLastName = "Booking",
                           guestEmail = $"policy-booking-{Guid.NewGuid():N}@example.test",
                           guestPhone = "+2348000000041",
                           checkIn = DateTime.UtcNow.Date.AddDays(50),
                           checkOut = DateTime.UtcNow.Date.AddDays(52),
                           adultCount = 1,
                           childCount = 0,
                           paymentMethod = "directTransfer",
                           acceptPrivacyPolicy = true,
                           privacyPolicyVersion = "local-v1",
                           acceptBookingTerms = true,
                           bookingTermsVersion = "local-v1"
                       }))
            using (var response = await _fixture.Client.SendAsync(acceptedBooking))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using (var missingRegistrationAcceptance = PublicJson(
                       HttpMethod.Post,
                       "/api/auth/register",
                       new
                       {
                           firstName = "Policy",
                           lastName = "Account",
                           email = $"policy-account-{Guid.NewGuid():N}@example.test",
                           password = "PolicyAccount123!",
                           phone = "+2348000000042"
                       }))
            using (var response = await _fixture.Client.SendAsync(missingRegistrationAcceptance))
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            configuration["Privacy:RequirePolicyAcceptance"] = previous;
        }
    }

    [Fact]
    public async Task Department_permissions_block_housekeeping_from_guest_and_reservation_data()
    {
        var housekeeping = await _fixture.CreateUserAsync(UserRole.Staff, "Housekeeping");
        var frontDesk = await _fixture.CreateUserAsync(UserRole.Staff, "FrontDesk");

        using (var request = Authorized(HttpMethod.Get, "/api/bookings", housekeeping))
        using (var response = await _fixture.Client.SendAsync(request))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using (var request = Authorized(HttpMethod.Get, "/api/guests", housekeeping))
        using (var response = await _fixture.Client.SendAsync(request))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using (var request = Authorized(HttpMethod.Get, "/api/bookings", frontDesk))
        using (var response = await _fixture.Client.SendAsync(request))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Password_reset_revokes_realtime_connections_and_old_security_stamp()
    {
        var staff = await _fixture.CreateUserAsync(UserRole.Staff, "FrontDesk");
        string resetToken;
        string email;
        string oldStamp;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(staff.Id.ToString());
            Assert.NotNull(user);
            email = user.Email!;
            oldStamp = user.SecurityStamp!;
            resetToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
                await userManager.GeneratePasswordResetTokenAsync(user)));
        }

        var registry = _fixture.Services.GetRequiredService<StaffConnectionRegistry>();
        var groups = new RecordingGroupManager();
        Assert.True(await registry.TryRegisterAsync(
            staff.Id,
            "password-reset-connection",
            oldStamp,
            groups));

        using var request = PublicJson(
            HttpMethod.Post,
            "/api/auth/reset-password",
            new
            {
                email,
                token = resetToken,
                newPassword = "ChangedPassword123!",
                confirmNewPassword = "ChangedPassword123!"
            });
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, registry.GetActiveConnectionCount(staff.Id));
        Assert.False(await registry.TryRegisterAsync(
            staff.Id,
            "old-stamp-connection",
            oldStamp,
            groups));
        using var staleHttpRequest = Authorized(HttpMethod.Get, "/api/bookings", staff);
        using var staleHttpResponse = await _fixture.Client.SendAsync(staleHttpRequest);
        Assert.Equal(HttpStatusCode.Forbidden, staleHttpResponse.StatusCode);
        Assert.Contains("SESSION_REVOKED", await staleHttpResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detailed_health_requires_admin_but_readiness_remains_public()
    {
        using (var response = await _fixture.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using (var response = await _fixture.Client.GetAsync("/health/ready"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var request = Authorized(HttpMethod.Get, "/api/health", _fixture.Admin);
        using var adminResponse = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public void Anonymous_catalogue_reads_have_explicit_rate_limit_metadata()
    {
        Assert.NotNull(typeof(RoomsController)
            .GetMethod(nameof(RoomsController.GetRooms))!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .SingleOrDefault());
        Assert.NotNull(typeof(RoomsController)
            .GetMethod(nameof(RoomsController.SearchRooms))!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .SingleOrDefault());
        Assert.NotNull(typeof(AddOnsController)
            .GetMethod(nameof(AddOnsController.GetAll))!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .SingleOrDefault());
    }

    [Fact]
    public async Task Privacy_export_request_resolution_and_audit_work_end_to_end()
    {
        var guest = await _fixture.LinkGuestProfileAsync(_fixture.ClientUser);

        using (var policies = await _fixture.Client.GetAsync("/api/privacy/policies/current"))
        {
            Assert.Equal(HttpStatusCode.OK, policies.StatusCode);
            Assert.Contains("local-v1", await policies.Content.ReadAsStringAsync());
        }

        using (var create = AuthorizedJson(
                   HttpMethod.Post,
                   "/api/privacy/requests",
                   _fixture.ClientUser,
                   new { type = "access", details = "Please provide a copy of my stored personal data." }))
        using (var response = await _fixture.Client.SendAsync(create))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Guid requestId;
        using (var mine = Authorized(HttpMethod.Get, "/api/privacy/requests/mine", _fixture.ClientUser))
        using (var mineResponse = await _fixture.Client.SendAsync(mine))
        {
            Assert.Equal(HttpStatusCode.OK, mineResponse.StatusCode);
            using var json = JsonDocument.Parse(await mineResponse.Content.ReadAsStringAsync());
            requestId = json.RootElement[0].GetProperty("id").GetGuid();
        }

        using (var close = AuthorizedJson(
                   HttpMethod.Patch,
                   $"/api/privacy/requests/{requestId}/status",
                   _fixture.Admin,
                   new
                   {
                       status = "completed",
                       resolutionNotes = "Identity verified and the requested export was supplied securely."
                   }))
        using (var response = await _fixture.Client.SendAsync(close))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var export = Authorized(HttpMethod.Get, "/api/privacy/export", _fixture.ClientUser))
        using (var response = await _fixture.Client.SendAsync(export))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(guest.Email, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(2, await _fixture.WithDbAsync(db => db.AuditLogs.CountAsync(log =>
            log.EntityId == requestId.ToString() &&
            (log.Action == "PRIVACY_REQUEST_CREATED" ||
             log.Action == "PRIVACY_REQUEST_STATUS_CHANGED"))));
    }

    [Fact]
    public async Task Retention_worker_anonymizes_only_expired_unlinked_guests()
    {
        var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var guestId = $"GS-{unique[..16]}";
        await _fixture.WithDbAsync(async db =>
        {
            db.Guests.Add(new Guest
            {
                Id = guestId,
                FirstName = "Old",
                LastName = "Anonymous",
                Email = $"old-{unique.ToLowerInvariant()}@example.test",
                Phone = "+2348000000044",
                CreatedAt = DateTime.UtcNow.AddDays(-400)
            });
            await db.SaveChangesAsync();
            return true;
        });

        var worker = new PrivacyRetentionWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PrivacySettings
            {
                EnableRetentionWorker = true,
                GuestRetentionDays = 365
            }),
            _fixture.Services.GetRequiredService<ILogger<PrivacyRetentionWorker>>());

        Assert.True(await worker.SweepOnceAsync() >= 1);
        var stored = await _fixture.WithDbAsync(db => db.Guests
            .AsNoTracking()
            .SingleAsync(item => item.Id == guestId));
        Assert.Equal("Former", stored.FirstName);
        Assert.Equal("REDACTED", stored.Phone);
        Assert.NotNull(stored.AnonymizedAtUtc);
    }

    private static object BookingBody(Guid roomId, int adults, int children) => new
    {
        roomId,
        guestFirstName = "Occupancy",
        guestLastName = "Tester",
        guestEmail = $"occupancy-{Guid.NewGuid():N}@example.test",
        guestPhone = "+2348000000040",
        checkIn = DateTime.UtcNow.Date.AddDays(40),
        checkOut = DateTime.UtcNow.Date.AddDays(42),
        adultCount = adults,
        childCount = children,
        paymentMethod = "directTransfer"
    };

    private static HttpRequestMessage PublicJson(HttpMethod method, string path, object body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private static HttpRequestMessage AuthorizedJson(
        HttpMethod method,
        string path,
        TestUser actor,
        object body)
    {
        var request = Authorized(method, path, actor);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, TestUser actor)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
