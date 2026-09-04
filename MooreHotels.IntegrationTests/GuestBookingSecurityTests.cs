using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class GuestBookingSecurityTests
{
    private readonly ManualTransferTestFixture _fixture;

    public GuestBookingSecurityTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task New_booking_uses_a_hashed_guest_token_and_rejects_code_plus_email_lookup()
    {
        var created = await CreatePublicBookingAsync();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", created.AccessToken);
        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.BookingCode == created.BookingCode));
        Assert.NotEqual(created.AccessToken, stored.GuestAccessTokenHash);
        Assert.True(BookingGuestAccess.Verify(created.AccessToken, stored.GuestAccessTokenHash));

        using var emailOnly = PublicRequest(
            HttpMethod.Get,
            $"/api/bookings/lookup?code={created.BookingCode}&email={Uri.EscapeDataString(created.Email)}");
        using var emailOnlyResponse = await _fixture.Client.SendAsync(emailOnly);
        Assert.Equal(HttpStatusCode.NotFound, emailOnlyResponse.StatusCode);

        using var secure = PublicRequest(HttpMethod.Get, $"/api/bookings/lookup?code={created.BookingCode}");
        secure.Headers.Add("X-Booking-Access-Token", created.AccessToken);
        using var secureResponse = await _fixture.Client.SendAsync(secure);
        Assert.Equal(HttpStatusCode.OK, secureResponse.StatusCode);
    }

    [Fact]
    public async Task Invoice_requires_booking_access_and_disables_response_caching()
    {
        var created = await CreatePublicBookingAsync();
        var invoicePath = $"/api/bookings/{created.BookingCode}/invoice.pdf";

        using var anonymous = PublicRequest(HttpMethod.Get, invoicePath);
        using var anonymousResponse = await _fixture.Client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.NotFound, anonymousResponse.StatusCode);

        using var invalid = PublicRequest(HttpMethod.Get, invoicePath);
        invalid.Headers.Add("X-Booking-Access-Token", BookingGuestAccess.GenerateToken());
        using var invalidResponse = await _fixture.Client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.NotFound, invalidResponse.StatusCode);

        using var secure = PublicRequest(HttpMethod.Get, invoicePath);
        secure.Headers.Add("X-Booking-Access-Token", created.AccessToken);
        using var secureResponse = await _fixture.Client.SendAsync(secure);
        Assert.Equal(HttpStatusCode.OK, secureResponse.StatusCode);
        Assert.Equal("application/pdf", secureResponse.Content.Headers.ContentType?.MediaType);
        Assert.True(secureResponse.Headers.CacheControl?.NoStore);

        using var nonOwner = PublicRequest(HttpMethod.Get, invoicePath);
        nonOwner.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _fixture.ClientUser.Token);
        using var nonOwnerResponse = await _fixture.Client.SendAsync(nonOwner);
        Assert.Equal(HttpStatusCode.NotFound, nonOwnerResponse.StatusCode);

        var originalGuestId = await _fixture.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(item => item.Id == _fixture.ClientUser.Id);
            var booking = await db.Bookings.SingleAsync(item => item.Id == created.BookingId);
            var previousGuestId = user.GuestId;
            user.GuestId = booking.GuestId;
            await db.SaveChangesAsync();
            return previousGuestId;
        });

        try
        {
            using var owner = PublicRequest(HttpMethod.Get, invoicePath);
            owner.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _fixture.ClientUser.Token);
            using var ownerResponse = await _fixture.Client.SendAsync(owner);
            Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                var user = await db.Users.SingleAsync(item => item.Id == _fixture.ClientUser.Id);
                user.GuestId = originalGuestId;
                await db.SaveChangesAsync();
                return true;
            });
        }

        using var staff = PublicRequest(HttpMethod.Get, invoicePath);
        staff.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _fixture.Staff.Token);
        using var staffResponse = await _fixture.Client.SendAsync(staff);
        Assert.Equal(HttpStatusCode.OK, staffResponse.StatusCode);
    }

    [Fact]
    public async Task Expired_guest_token_fails_closed()
    {
        var created = await CreatePublicBookingAsync();
        await _fixture.WithDbAsync(async db =>
        {
            var booking = await db.Bookings.SingleAsync(item => item.Id == created.BookingId);
            booking.GuestAccessTokenIssuedAtUtc = DateTime.UtcNow.AddHours(-2);
            booking.GuestAccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            return true;
        });

        using var lookup = PublicRequest(
            HttpMethod.Get,
            $"/api/bookings/lookup?code={created.BookingCode}");
        lookup.Headers.Add("X-Booking-Access-Token", created.AccessToken);
        using var response = await _fixture.Client.SendAsync(lookup);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Access_link_request_is_non_enumerating_rotates_secure_token_and_queues_email()
    {
        var created = await CreatePublicBookingAsync();
        _fixture.Email.Reset();
        using var request = PublicJsonRequest(
            "/api/bookings/access-link",
            new { bookingCode = created.BookingCode, email = created.Email });

        using var response = await _fixture.Client.SendAsync(request);
        await _fixture.FlushEmailOutboxAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accessEmail = Assert.Single(_fixture.Email.Messages, message =>
            message.Template == "BookingAccessLink" &&
            message.BookingCode == created.BookingCode);
        Assert.NotNull(accessEmail.Link);
        var replacementToken = Uri.UnescapeDataString(
            accessEmail.Link!.Split("#accessToken=", StringSplitOptions.None)[1]);
        Assert.NotEqual(created.AccessToken, replacementToken);
        using var originalLink = PublicRequest(HttpMethod.Get, $"/api/bookings/lookup?code={created.BookingCode}");
        originalLink.Headers.Add("X-Booking-Access-Token", created.AccessToken);
        using var originalLinkResponse = await _fixture.Client.SendAsync(originalLink);
        Assert.Equal(HttpStatusCode.NotFound, originalLinkResponse.StatusCode);
        using var replacementLink = PublicRequest(HttpMethod.Get, $"/api/bookings/lookup?code={created.BookingCode}");
        replacementLink.Headers.Add("X-Booking-Access-Token", replacementToken);
        using var replacementLinkResponse = await _fixture.Client.SendAsync(replacementLink);
        Assert.Equal(HttpStatusCode.OK, replacementLinkResponse.StatusCode);
        Assert.Equal(1, await _fixture.WithDbAsync(db => db.AuditLogs.CountAsync(log =>
            log.Action == "BOOKING_ACCESS_LINK_REQUESTED" &&
            log.EntityId == created.BookingId.ToString())));

        var tokenWindow = await _fixture.WithDbAsync(db => db.Bookings
            .Where(booking => booking.Id == created.BookingId)
            .Select(booking => new
            {
                booking.GuestAccessTokenIssuedAtUtc,
                booking.GuestAccessTokenExpiresAtUtc
            })
            .SingleAsync());
        Assert.NotNull(tokenWindow.GuestAccessTokenIssuedAtUtc);
        Assert.InRange(
            tokenWindow.GuestAccessTokenExpiresAtUtc!.Value -
            tokenWindow.GuestAccessTokenIssuedAtUtc!.Value,
            TimeSpan.FromHours(1.99),
            TimeSpan.FromHours(2.01));

        _fixture.Email.Reset();
        using (var repeatedRequest = PublicJsonRequest(
                   "/api/bookings/access-link",
                   new { bookingCode = created.BookingCode, email = created.Email }))
        using (var repeatedResponse = await _fixture.Client.SendAsync(repeatedRequest))
        {
            Assert.Equal(HttpStatusCode.Accepted, repeatedResponse.StatusCode);
        }
        await _fixture.FlushEmailOutboxAsync();
        Assert.DoesNotContain(_fixture.Email.Messages, message =>
            message.Template == "BookingAccessLink" &&
            message.BookingCode == created.BookingCode);

        using var unknown = PublicJsonRequest(
            "/api/bookings/access-link",
            new { bookingCode = "MHS999999", email = "unknown@example.test" });
        using var unknownResponse = await _fixture.Client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.Accepted, unknownResponse.StatusCode);
        Assert.Equal(
            await response.Content.ReadAsStringAsync(),
            await unknownResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Guest_cancellation_is_token_protected_audited_and_transactional()
    {
        var created = await CreatePublicBookingAsync();
        using var invalid = PublicJsonRequest(
            "/api/bookings/guest/cancel",
            new
            {
                bookingCode = created.BookingCode,
                email = created.Email,
                guestAccessToken = BookingGuestAccess.GenerateToken(),
                reason = "Plans changed"
            });
        using var invalidResponse = await _fixture.Client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.Forbidden, invalidResponse.StatusCode);

        using var valid = PublicJsonRequest(
            "/api/bookings/guest/cancel",
            new
            {
                bookingCode = created.BookingCode,
                guestAccessToken = created.AccessToken,
                reason = "Plans changed"
            });
        using var validResponse = await _fixture.Client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);

        var state = await _fixture.WithDbAsync(async db => new
        {
            Booking = await db.Bookings.AsNoTracking()
                .SingleAsync(item => item.BookingCode == created.BookingCode),
            Audit = await db.AuditLogs.AsNoTracking()
                .SingleAsync(item => item.Action == "GUEST_BOOKING_CANCELLED" &&
                                     item.EntityId == created.BookingId.ToString()),
            QueuedEmails = await db.EmailOutboxMessages.CountAsync(message =>
                message.Template == "Cancellation")
        });
        Assert.Equal(BookingStatus.Cancelled, state.Booking.Status);
        Assert.NotNull(state.Booking.GuestAccessTokenRevokedAtUtc);
        Assert.Contains("RequestId", state.Audit.NewDataJson);
        Assert.DoesNotContain(created.AccessToken, state.Audit.NewDataJson);
        Assert.True(
            state.QueuedEmails >= 1 ||
            _fixture.Email.Messages.Any(message =>
                message.Template == "Cancellation" &&
                message.BookingCode == created.BookingCode));
    }

    [Fact]
    public async Task Payment_confirmation_and_guest_cancellation_never_commit_an_impossible_state()
    {
        var created = await CreatePublicBookingAsync();
        using var cancel = PublicJsonRequest(
            "/api/bookings/guest/cancel",
            new
            {
                bookingCode = created.BookingCode,
                guestAccessToken = created.AccessToken,
                reason = "Concurrent cancellation"
            });
        using var confirm = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/bookings/{created.BookingCode}/confirm-transfer")
        {
            Content = JsonContent.Create(new
            {
                confirmationText = "ACCEPT",
                confirmationMethod = "TypedAcknowledgement",
                transactionReference = "IGNORED"
            })
        };
        confirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.Admin.Token);
        AddEnvironmentHeader(confirm);

        var responses = await Task.WhenAll(
            _fixture.Client.SendAsync(cancel),
            _fixture.Client.SendAsync(confirm));
        foreach (var response in responses) response.Dispose();

        var stored = await _fixture.WithDbAsync(db => db.Bookings.AsNoTracking()
            .SingleAsync(item => item.BookingCode == created.BookingCode));
        Assert.True(
            stored.Status == BookingStatus.Confirmed && stored.PaymentStatus == PaymentStatus.Paid ||
            stored.Status == BookingStatus.Cancelled &&
            stored.PaymentStatus is PaymentStatus.AwaitingVerification or PaymentStatus.RefundPending);
    }

    [Fact]
    public async Task Booking_and_guest_roll_back_when_required_email_cannot_be_queued()
    {
        var room = await _fixture.CreateRoomAsync();
        var email = $"must-rollback-{Guid.NewGuid():N}@example.test";
        await _fixture.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION fail_booking_email_outbox() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'forced email outbox failure';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_booking_email_outbox_trigger
                BEFORE INSERT ON email_outbox
                FOR EACH ROW EXECUTE FUNCTION fail_booking_email_outbox();
                """);
            return true;
        });

        try
        {
            using var request = PublicJsonRequest(
                "/api/bookings",
                new
                {
                    roomId = room.Id,
                    guestFirstName = "Rollback",
                    guestLastName = "Guest",
                    guestEmail = email,
                    guestPhone = "+2348000000004",
                    checkIn = DateTime.UtcNow.Date.AddDays(25),
                    checkOut = DateTime.UtcNow.Date.AddDays(26),
                    paymentMethod = "directTransfer"
                });
            using var response = await _fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(0, await _fixture.WithDbAsync(db => db.Guests.CountAsync(
                guest => guest.Email == email)));
            Assert.Equal(0, await _fixture.WithDbAsync(db => db.Bookings.CountAsync(
                booking => booking.RoomId == room.Id)));
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER IF EXISTS fail_booking_email_outbox_trigger ON email_outbox;
                    DROP FUNCTION IF EXISTS fail_booking_email_outbox();
                    """);
                return true;
            });
        }
    }

    private async Task<CreatedBooking> CreatePublicBookingAsync()
    {
        var room = await _fixture.CreateRoomAsync();
        var email = $"secure-booking-{Guid.NewGuid():N}@example.test";
        using var request = PublicJsonRequest(
            "/api/bookings",
            new
            {
                roomId = room.Id,
                guestFirstName = "Secure",
                guestLastName = "Guest",
                guestEmail = email,
                guestPhone = "+2348000000003",
                checkIn = DateTime.UtcNow.Date.AddDays(20),
                checkOut = DateTime.UtcNow.Date.AddDays(22),
                paymentMethod = "directTransfer",
                notes = "Guest access security test"
            });
        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new CreatedBooking(
            json.RootElement.GetProperty("id").GetGuid(),
            json.RootElement.GetProperty("bookingCode").GetString()!,
            email,
            json.RootElement.GetProperty("guestAccessToken").GetString()!);
    }

    private static HttpRequestMessage PublicJsonRequest(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        AddEnvironmentHeader(request);
        return request;
    }

    private static HttpRequestMessage PublicRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        AddEnvironmentHeader(request);
        return request;
    }

    private static void AddEnvironmentHeader(HttpRequestMessage request) =>
        request.Headers.Add("X-Moore-App-Environment", "local");

    private sealed record CreatedBooking(
        Guid BookingId,
        string BookingCode,
        string Email,
        string AccessToken);
}
