using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class FirstFiveProductionFixTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ManualTransferTestFixture _fixture;

    public FirstFiveProductionFixTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Exhausted_email_marks_diagnostics_degraded_without_failing_readiness()
    {
        Guid messageId;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MooreHotels.Infrastructure.Persistence.MooreHotelsDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutbox>();
            var message = outbox.Create(
                TransactionalEmailTemplates.PasswordReset,
                "health-check@example.test",
                new PasswordResetEmail("Health Check", "https://example.test/reset"));
            message.AttemptCount = 12;
            db.EmailOutboxMessages.Add(message);
            await db.SaveChangesAsync();
            messageId = message.Id;
        }

        try
        {
            using var diagnosticsRequest = AuthorizedRequest(HttpMethod.Get, "/api/health");
            using var diagnostics = await _fixture.Client.SendAsync(diagnosticsRequest);
            Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
            using var payload = JsonDocument.Parse(await diagnostics.Content.ReadAsStringAsync());
            Assert.Equal("Degraded", payload.RootElement.GetProperty("status").GetString());
            Assert.True(payload.RootElement.GetProperty("emailQueue").GetProperty("exhausted").GetInt32() >= 1);

            using var readiness = await _fixture.Client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                await db.EmailOutboxMessages
                    .Where(message => message.Id == messageId)
                    .ExecuteDeleteAsync();
                return true;
            });
        }
    }

    [Fact]
    public async Task Booking_verification_is_throttled_single_use_and_consumed_with_the_booking()
    {
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        var previous = configuration["Runtime:RequirePublicBookingEmailVerification"];
        configuration["Runtime:RequirePublicBookingEmailVerification"] = "true";
        var email = $"verified-{Guid.NewGuid():N}@example.test";

        try
        {
            using (var request = PublicJsonRequest(
                       "/api/bookings/verification/request",
                       new { email }))
            using (var response = await _fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            }

            using (var request = PublicJsonRequest(
                       "/api/bookings/verification/request",
                       new { email }))
            using (var response = await _fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            }

            Assert.Equal(1, await _fixture.WithDbAsync(db =>
                db.BookingEmailVerifications.CountAsync(item => item.Email == email)));

            var firstRoom = await _fixture.CreateRoomAsync();
            using (var request = BookingRequest(firstRoom.Id, email, null))
            using (var response = await _fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            var token = BookingGuestAccess.GenerateToken();
            var verificationId = Guid.NewGuid();
            await _fixture.WithDbAsync(async db =>
            {
                db.BookingEmailVerifications.Add(new BookingEmailVerification
                {
                    Id = verificationId,
                    Email = email,
                    TokenHash = BookingGuestAccess.Hash(token),
                    CreatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
                });
                await db.SaveChangesAsync();
                return true;
            });

            using (var request = BookingRequest(firstRoom.Id, email, token))
            using (var response = await _fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.NotNull(await _fixture.WithDbAsync(db => db.BookingEmailVerifications
                .Where(item => item.Id == verificationId)
                .Select(item => item.ConsumedAtUtc)
                .SingleAsync()));

            var secondRoom = await _fixture.CreateRoomAsync();
            using var replay = BookingRequest(secondRoom.Id, email, token);
            using var replayResponse = await _fixture.Client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.BadRequest, replayResponse.StatusCode);
            Assert.Equal(1, await _fixture.WithDbAsync(db => db.Bookings.CountAsync(
                booking => booking.Guest != null && booking.Guest.Email == email)));
        }
        finally
        {
            configuration["Runtime:RequirePublicBookingEmailVerification"] = previous;
        }
    }

    [Fact]
    public async Task Registration_responses_do_not_reveal_existing_email_addresses()
    {
        var existingEmail = await _fixture.WithDbAsync(db => db.Users
            .Where(user => user.Id == _fixture.ClientUser.Id)
            .Select(user => user.Email!)
            .SingleAsync());
        var unknownWeakEmail = $"weak-{Guid.NewGuid():N}@example.test";

        using var weakExisting = await RegisterAsync(existingEmail, "aaaaaaaa");
        using var weakUnknown = await RegisterAsync(unknownWeakEmail, "aaaaaaaa");
        Assert.Equal(HttpStatusCode.BadRequest, weakExisting.StatusCode);
        Assert.Equal(weakExisting.StatusCode, weakUnknown.StatusCode);
        Assert.Contains("uppercase", await weakExisting.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uppercase", await weakUnknown.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var newEmail = $"register-{Guid.NewGuid():N}@example.test";
        using var strongExisting = await RegisterAsync(existingEmail, "Registration123!");
        using var strongUnknown = await RegisterAsync(newEmail, "Registration123!");
        Assert.Equal(HttpStatusCode.Accepted, strongExisting.StatusCode);
        Assert.Equal(strongExisting.StatusCode, strongUnknown.StatusCode);
        Assert.Equal(
            await strongExisting.Content.ReadAsStringAsync(),
            await strongUnknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ledger_finds_history_beyond_old_caps_and_uses_a_stable_composite_cursor()
    {
        var room = await _fixture.CreateRoomAsync();
        var unique = Guid.NewGuid().ToString("N");
        var guest = new Guest
        {
            Id = $"GS-{unique[..16].ToUpperInvariant()}",
            FirstName = "Deep",
            LastName = "History",
            Email = $"ledger-{unique}@example.test",
            Phone = "+2348000000099"
        };
        var codePrefix = $"LG{unique[..8].ToUpperInvariant()}";
        var seed = $"ledger-{unique}-";
        var createdBase = DateTime.UtcNow.AddYears(-10);
        await _fixture.WithDbAsync(async db =>
        {
            db.Guests.Add(guest);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO bookings
                     ("Id", "BookingCode", "RoomId", "GuestId", "CheckIn", "CheckOut",
                      "Status", "Amount", "PaymentStatus", "PaymentMethod", "StatusHistoryJson", "CreatedAt")
                 SELECT
                     md5({seed} || series::text)::uuid,
                     {codePrefix} || lpad(series::text, 6, '0'),
                     {room.Id},
                     {guest.Id},
                     {createdBase.AddDays(1)},
                     {createdBase.AddDays(2)},
                     'Confirmed',
                     1000.00,
                     'Paid',
                     'DirectTransfer',
                     '[]'::jsonb,
                     {createdBase} + series * interval '1 second'
                 FROM generate_series(0, 2001) AS series
                 """);
            return true;
        });

        try
        {
            using (var request = AuthorizedRequest(
                       HttpMethod.Get,
                       $"/api/operations/ledger?search={codePrefix}000000&limit=10"))
            using (var response = await _fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var entries = await response.Content.ReadFromJsonAsync<List<OperationLogEntryDto>>(JsonOptions);
                Assert.Contains(entries!, entry => entry.VerificationInfo == $"{codePrefix}000000");
            }

            var timestamp = DateTime.UtcNow.AddYears(-5);
            var visitIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            await _fixture.WithDbAsync(async db =>
            {
                db.VisitRecords.AddRange(visitIds.Select((id, index) => new VisitRecord
                {
                    Id = id,
                    GuestId = guest.Id,
                    GuestName = $"Cursor Guest {index}",
                    RoomId = room.Id,
                    RoomNumber = room.RoomNumber,
                    BookingCode = $"{codePrefix}{index:D6}",
                    Action = "CHECK_IN",
                    Timestamp = timestamp,
                    AuthorizedBy = "Ledger Test"
                }));
                await db.SaveChangesAsync();
                return true;
            });

            List<OperationLogEntryDto> firstPage;
            using (var request = AuthorizedRequest(
                       HttpMethod.Get,
                       "/api/operations/ledger?filter=CHECK%20IN&limit=2"))
            using (var response = await _fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                firstPage = await response.Content.ReadFromJsonAsync<List<OperationLogEntryDto>>(JsonOptions) ?? [];
            }

            Assert.Equal(2, firstPage.Count);
            var cursor = firstPage[^1];
            var cursorTime = Uri.EscapeDataString(cursor.Timestamp.ToUniversalTime().ToString("O"));
            using var nextRequest = AuthorizedRequest(
                HttpMethod.Get,
                $"/api/operations/ledger?filter=CHECK%20IN&limit=2&beforeUtc={cursorTime}&beforeId={cursor.Id}");
            using var nextResponse = await _fixture.Client.SendAsync(nextRequest);
            Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
            var nextPage = await nextResponse.Content.ReadFromJsonAsync<List<OperationLogEntryDto>>(JsonOptions) ?? [];
            Assert.Single(nextPage);
            Assert.DoesNotContain(nextPage[0].Id, firstPage.Select(entry => entry.Id));
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                await db.Bookings
                    .Where(booking => EF.Functions.Like(booking.BookingCode, codePrefix + "%"))
                    .ExecuteDeleteAsync();
                await db.VisitRecords
                    .Where(record => record.GuestId == guest.Id)
                    .ExecuteDeleteAsync();
                await db.Guests
                    .Where(item => item.Id == guest.Id)
                    .ExecuteDeleteAsync();
                return true;
            });
        }
    }

    private async Task<HttpResponseMessage> RegisterAsync(string email, string password)
    {
        using var request = PublicJsonRequest(
            "/api/auth/register",
            new
            {
                firstName = "Privacy",
                lastName = "Test",
                email,
                password,
                phone = "+2348000000011"
            });
        return await _fixture.Client.SendAsync(request);
    }

    private HttpRequestMessage AuthorizedRequest(HttpMethod method, string path)
    {
        var request = PublicRequest(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _fixture.Admin.Token);
        return request;
    }

    private static HttpRequestMessage BookingRequest(
        Guid roomId,
        string email,
        string? emailVerificationToken) =>
        PublicJsonRequest(
            "/api/bookings",
            new
            {
                roomId,
                guestFirstName = "Verified",
                guestLastName = "Guest",
                guestEmail = email,
                guestPhone = "+2348000000010",
                checkIn = DateTime.UtcNow.Date.AddDays(30),
                checkOut = DateTime.UtcNow.Date.AddDays(32),
                adultCount = 2,
                childCount = 0,
                paymentMethod = "directTransfer",
                notes = "Booking email verification test",
                emailVerificationToken
            });

    private static HttpRequestMessage PublicJsonRequest(string path, object body)
    {
        var request = PublicRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage PublicRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }
}
