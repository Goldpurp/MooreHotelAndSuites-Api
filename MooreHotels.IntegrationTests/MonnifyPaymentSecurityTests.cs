using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Services;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class MonnifyPaymentSecurityTests
{
    private const string WebhookSecret = "integration-webhook-signing-secret";
    private readonly ManualTransferTestFixture _fixture;

    public MonnifyPaymentSecurityTests(ManualTransferTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Booking_creation_persists_server_initialized_checkout_for_safe_resume()
    {
        _fixture.Monnify.Reset();
        var room = await _fixture.CreateRoomAsync();
        var unique = Guid.NewGuid().ToString("N");
        using var response = await _fixture.Client.PostAsJsonAsync(
            "/api/bookings",
            new
            {
                roomId = room.Id,
                guestFirstName = "Monnify",
                guestLastName = "Guest",
                guestEmail = $"monnify-{unique}@example.test",
                guestPhone = "+2348000000002",
                checkIn = DateTime.UtcNow.Date.AddDays(20),
                checkOut = DateTime.UtcNow.Date.AddDays(22),
                paymentMethod = "monnify",
                notes = "Secure checkout test"
            });

        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var bookingCode = payload.RootElement
            .GetProperty("bookingCode")
            .GetString();
        var paymentUrl = payload.RootElement
            .GetProperty("paymentUrl")
            .GetString();
        Assert.NotNull(bookingCode);
        Assert.StartsWith("MHS", bookingCode);
        Assert.StartsWith("https://sandbox.sdk.monnify.com/", paymentUrl);
        Assert.Equal(1, _fixture.Monnify.InitializationCalls);

        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.BookingCode == bookingCode));
        Assert.NotNull(stored.TransactionReference);
        Assert.NotNull(stored.PaymentProviderReference);
        Assert.Equal(paymentUrl, stored.PaymentCheckoutUrl);
        Assert.Equal(
            stored.CreatedAt.Add(MonnifyPaymentPolicy.HostedCheckoutWindow),
            stored.PaymentCheckoutExpiresAtUtc);
    }

    [Fact]
    public async Task Disabled_monnify_rejects_booking_before_guest_or_booking_is_created()
    {
        var configuration =
            _fixture.Services.GetRequiredService<IConfiguration>();
        var previous = configuration["MonnifySettings:Enabled"];
        var room = await _fixture.CreateRoomAsync();
        var unique = Guid.NewGuid().ToString("N");
        var email = $"disabled-monnify-{unique}@example.test";

        try
        {
            configuration["MonnifySettings:Enabled"] = "false";
            await using var scope = _fixture.Services.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IBookingService>();
            var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
                service.CreateBookingAsync(
                    new CreateBookingRequest(
                        room.Id,
                        "Disabled",
                        "Gateway",
                        email,
                        "+2348000000002",
                        DateTime.UtcNow.Date.AddDays(20),
                        DateTime.UtcNow.Date.AddDays(22),
                        PaymentMethod.Monnify,
                        null)));
            Assert.Contains(
                "Online payment is temporarily unavailable",
                exception.Message,
                StringComparison.Ordinal);

            var persisted = await _fixture.WithDbAsync(async db => new
            {
                Guests = await db.Guests.CountAsync(item => item.Email == email),
                Bookings = await db.Bookings.CountAsync(
                    item => item.Guest != null && item.Guest.Email == email)
            });
            Assert.Equal(0, persisted.Guests);
            Assert.Equal(0, persisted.Bookings);
        }
        finally
        {
            configuration["MonnifySettings:Enabled"] = previous;
        }
    }

    [Fact]
    public async Task Admin_verification_uses_only_server_reference_and_records_atomic_audit()
    {
        _fixture.Monnify.Reset();
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid);
        var verification = await CreateValidVerificationAsync(booking);
        _fixture.Monnify.SetVerification(
            booking.TransactionReference!,
            verification);

        using var response = await VerifyAsAsync(
            booking,
            _fixture.Admin,
            "?transactionReference=ATTACKER-CONTROLLED");

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, _fixture.Monnify.VerificationCalls);
        var state = await _fixture.WithDbAsync(async db => new
        {
            Booking = await db.Bookings
                .AsNoTracking()
                .SingleAsync(item => item.Id == booking.Id),
            Transactions = await db.MonnifyTransactions
                .AsNoTracking()
                .Where(item => item.BookingId == booking.Id)
                .ToListAsync(),
            Audits = await db.AuditLogs
                .AsNoTracking()
                .Where(item =>
                    item.EntityId == booking.Id.ToString() &&
                    item.Action == "MONNIFY_PAYMENT_CONFIRMED")
                .ToListAsync()
        });

        Assert.Equal(PaymentStatus.Paid, state.Booking.PaymentStatus);
        Assert.Equal(BookingStatus.Confirmed, state.Booking.Status);
        Assert.Equal(
            MonnifyPaymentProcessor.ConfirmationMethod,
            state.Booking.PaymentConfirmationMethod);
        Assert.Equal(
            booking.PaymentProviderReference,
            state.Booking.PaymentProviderReference);
        var transaction = Assert.Single(state.Transactions);
        Assert.Equal(booking.TransactionReference, transaction.TransactionReference);
        Assert.Equal(booking.PaymentProviderReference, transaction.MonnifyReference);
        Assert.Equal("AdminVerification", transaction.Source);
        Assert.NotNull(transaction.VerifiedAtUtc);
        var audit = Assert.Single(state.Audits);
        Assert.Equal(_fixture.Admin.Id, audit.ProfileId);
        Assert.DoesNotContain(
            "password",
            audit.NewDataJson ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "token",
            audit.NewDataJson ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "secret",
            audit.NewDataJson ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("paymentReference")]
    [InlineData("providerReference")]
    [InlineData("bookingCode")]
    [InlineData("bookingId")]
    [InlineData("customerEmail")]
    [InlineData("paidAtTooOld")]
    [InlineData("paidAtFuture")]
    public async Task Mismatched_provider_data_never_confirms_booking(string mismatch)
    {
        _fixture.Monnify.Reset();
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid);
        var verification = await CreateValidVerificationAsync(booking);
        verification = mismatch switch
        {
            "status" => verification with { Status = "PARTIALLY_PAID" },
            "amount" => verification with { AmountPaid = booking.Amount - 1m },
            "currency" => verification with { CurrencyCode = "USD" },
            "paymentReference" => verification with
            {
                PaymentReference = $"OTHER-{Guid.NewGuid():N}"
            },
            "providerReference" => verification with
            {
                TransactionReference = $"MNFY|OTHER|{Guid.NewGuid():N}"
            },
            "bookingCode" => verification with { BookingCode = "MHS99999" },
            "bookingId" => verification with { BookingId = Guid.NewGuid() },
            "customerEmail" => verification with
            {
                CustomerEmail = "attacker@example.test"
            },
            "paidAtTooOld" => verification with
            {
                PaidAtUtc = booking.CreatedAt.AddMinutes(-6)
            },
            "paidAtFuture" => verification with
            {
                PaidAtUtc = DateTime.UtcNow.AddMinutes(6)
            },
            _ => throw new InvalidOperationException()
        };
        _fixture.Monnify.SetVerification(
            booking.TransactionReference!,
            verification);

        using var response = await VerifyAsAsync(booking, _fixture.Admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var state = await _fixture.WithDbAsync(async db => new
        {
            Booking = await db.Bookings
                .AsNoTracking()
                .SingleAsync(item => item.Id == booking.Id),
            TransactionCount = await db.MonnifyTransactions
                .CountAsync(item => item.BookingId == booking.Id),
            AuditCount = await db.AuditLogs.CountAsync(item =>
                item.EntityId == booking.Id.ToString() &&
                item.Action == "MONNIFY_PAYMENT_CONFIRMED")
        });
        Assert.Equal(PaymentStatus.Unpaid, state.Booking.PaymentStatus);
        Assert.Equal(BookingStatus.Pending, state.Booking.Status);
        Assert.Equal(0, state.TransactionCount);
        Assert.Equal(0, state.AuditCount);
    }

    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Client)]
    public async Task Staff_and_client_cannot_trigger_provider_verification(
        UserRole role)
    {
        _fixture.Monnify.Reset();
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid);
        var actor = role == UserRole.Staff
            ? _fixture.Staff
            : _fixture.ClientUser;

        using var response = await VerifyAsAsync(booking, actor);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _fixture.Monnify.VerificationCalls);
    }

    [Fact]
    public async Task Concurrent_and_replayed_verification_credits_booking_once()
    {
        _fixture.Monnify.Reset();
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid);
        _fixture.Monnify.SetVerification(
            booking.TransactionReference!,
            await CreateValidVerificationAsync(booking));

        var responses = await Task.WhenAll(
            VerifyAsAsync(booking, _fixture.Admin),
            VerifyAsAsync(booking, _fixture.Manager));
        foreach (var response in responses)
        {
            using (response)
            {
                response.EnsureSuccessStatusCode();
            }
        }
        using var replay = await VerifyAsAsync(booking, _fixture.Admin);
        replay.EnsureSuccessStatusCode();

        var counts = await _fixture.WithDbAsync(async db => new
        {
            Transactions = await db.MonnifyTransactions
                .CountAsync(item => item.BookingId == booking.Id),
            Audits = await db.AuditLogs.CountAsync(item =>
                item.EntityId == booking.Id.ToString() &&
                item.Action == "MONNIFY_PAYMENT_CONFIRMED")
        });
        Assert.Equal(1, counts.Transactions);
        Assert.Equal(1, counts.Audits);
    }

    [Fact]
    public async Task Forged_or_modified_webhook_is_rejected_before_provider_call()
    {
        _fixture.Monnify.Reset();
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid);
        var body = CreateWebhookBody(booking.TransactionReference!);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/payments/monnify-webhook")
        {
            Content = new StringContent(
                body.Replace(
                    booking.TransactionReference!,
                    $"FORGED-{Guid.NewGuid():N}",
                    StringComparison.Ordinal),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("monnify-signature", Sign(body));

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _fixture.Monnify.VerificationCalls);
        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == booking.Id));
        Assert.Equal(PaymentStatus.Unpaid, stored.PaymentStatus);
    }

    [Fact]
    public async Task Valid_webhook_confirms_once_and_replay_is_idempotent()
    {
        _fixture.Monnify.Reset();
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid);
        _fixture.Monnify.SetVerification(
            booking.TransactionReference!,
            await CreateValidVerificationAsync(booking));
        var body = CreateWebhookBody(booking.TransactionReference!);

        using var first = await PostWebhookAsync(body);
        using var replay = await PostWebhookAsync(body);

        first.EnsureSuccessStatusCode();
        replay.EnsureSuccessStatusCode();
        var state = await _fixture.WithDbAsync(async db => new
        {
            Booking = await db.Bookings
                .AsNoTracking()
                .SingleAsync(item => item.Id == booking.Id),
            Transactions = await db.MonnifyTransactions
                .CountAsync(item => item.BookingId == booking.Id),
            Audits = await db.AuditLogs.CountAsync(item =>
                item.EntityId == booking.Id.ToString() &&
                item.Action == "MONNIFY_PAYMENT_CONFIRMED")
        });
        Assert.Equal(PaymentStatus.Paid, state.Booking.PaymentStatus);
        Assert.Equal(1, state.Transactions);
        Assert.Equal(1, state.Audits);
    }

    [Fact]
    public async Task Payment_after_expiry_is_recorded_without_rebooking_room()
    {
        _fixture.Monnify.Reset();
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid,
            BookingStatus.Pending,
            DateTime.UtcNow.AddMinutes(-61));
        _fixture.Monnify.SetVerification(
            booking.TransactionReference!,
            await CreateValidVerificationAsync(booking));
        var body = CreateWebhookBody(booking.TransactionReference!);

        using var response = await PostWebhookAsync(body);

        response.EnsureSuccessStatusCode();
        var state = await _fixture.WithDbAsync(async db => new
        {
            Booking = await db.Bookings
                .AsNoTracking()
                .SingleAsync(item => item.Id == booking.Id),
            Transaction = await db.MonnifyTransactions
                .AsNoTracking()
                .SingleAsync(item => item.BookingId == booking.Id),
            Audit = await db.AuditLogs
                .AsNoTracking()
                .SingleAsync(item =>
                    item.EntityId == booking.Id.ToString() &&
                    item.Action ==
                    "MONNIFY_PAYMENT_RECEIVED_AFTER_EXPIRY")
        });
        Assert.Equal(PaymentStatus.Unpaid, state.Booking.PaymentStatus);
        Assert.NotEqual(BookingStatus.Confirmed, state.Booking.Status);
        Assert.Equal("PAID_AFTER_EXPIRY", state.Transaction.Status);
        Assert.Equal(BookingPaymentPolicy.SystemActorId, state.Audit.ProfileId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<MooreHotels.Application.Interfaces.Repositories.IBookingRepository>();
        Assert.False(await repository.IsRoomBookedAsync(
            booking.RoomId,
            booking.CheckIn,
            booking.CheckOut));
        await repository.CancelExpiredUnconfirmedAsync(DateTime.UtcNow);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_booking_and_payment_ledger()
    {
        _fixture.Monnify.Reset();
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid);
        _fixture.Monnify.SetVerification(
            booking.TransactionReference!,
            await CreateValidVerificationAsync(booking));
        await _fixture.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION fail_monnify_audit() RETURNS trigger AS $$
                BEGIN
                    IF NEW."Action" = 'MONNIFY_PAYMENT_CONFIRMED' THEN
                        RAISE EXCEPTION 'forced Monnify audit failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_monnify_audit_trigger
                BEFORE INSERT ON audit_logs
                FOR EACH ROW EXECUTE FUNCTION fail_monnify_audit();
                """);
            return true;
        });

        try
        {
            using var response = await VerifyAsAsync(
                booking,
                _fixture.Admin);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var state = await _fixture.WithDbAsync(async db => new
            {
                Booking = await db.Bookings
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == booking.Id),
                TransactionCount = await db.MonnifyTransactions
                    .CountAsync(item => item.BookingId == booking.Id)
            });
            Assert.Equal(PaymentStatus.Unpaid, state.Booking.PaymentStatus);
            Assert.Equal(BookingStatus.Pending, state.Booking.Status);
            Assert.Equal(0, state.TransactionCount);
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER IF EXISTS fail_monnify_audit_trigger ON audit_logs;
                    DROP FUNCTION IF EXISTS fail_monnify_audit();
                    """);
                return true;
            });
        }
    }

    [Fact]
    public void Webhook_security_uses_fixed_signature_and_source_allowlist()
    {
        const string body = """{"eventType":"SUCCESSFUL_TRANSACTION"}""";
        var signature = Sign(body);
        Assert.True(MonnifyWebhookSecurity.VerifySignature(
            body,
            signature,
            WebhookSecret));
        Assert.False(MonnifyWebhookSecurity.VerifySignature(
            $"{body} ",
            signature,
            WebhookSecret));
        Assert.False(MonnifyWebhookSecurity.VerifySignature(
            body,
            new string('0', 128),
            WebhookSecret));

        var settings = new MonnifySettings
        {
            EnforceWebhookIpAllowlist = true,
            AllowedWebhookIpAddresses = ["35.242.133.146"]
        };
        Assert.True(MonnifyWebhookSecurity.IsAllowedSource(
            IPAddress.Parse("35.242.133.146"),
            settings));
        Assert.True(MonnifyWebhookSecurity.IsAllowedSource(
            IPAddress.Parse("::ffff:35.242.133.146"),
            settings));
        Assert.False(MonnifyWebhookSecurity.IsAllowedSource(
            IPAddress.Parse("203.0.113.10"),
            settings));
        Assert.False(MonnifyWebhookSecurity.IsAllowedSource(null, settings));
    }

    [Fact]
    public async Task Payment_ledger_schema_does_not_duplicate_guest_pii()
    {
        var columns = await _fixture.WithDbAsync(db =>
            db.Database.SqlQueryRaw<string>(
                    """
                    SELECT column_name AS "Value"
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'monnify_transactions'
                    """)
                .ToListAsync());

        Assert.DoesNotContain("CustomerEmail", columns);
        Assert.DoesNotContain("CustomerName", columns);
        Assert.DoesNotContain("MerchantReference", columns);
    }

    private async Task<MonnifyVerificationResult> CreateValidVerificationAsync(
        Booking booking)
    {
        var guest = await _fixture.WithDbAsync(db => db.Guests
            .AsNoTracking()
            .SingleAsync(item => item.Id == booking.GuestId));
        return new MonnifyVerificationResult(
            booking.TransactionReference!,
            booking.PaymentProviderReference!,
            "PAID",
            booking.Amount,
            "NGN",
            booking.BookingCode,
            guest.Email,
            $"{guest.FirstName} {guest.LastName}",
            100m,
            booking.Amount - 100m,
            "ACCOUNT_TRANSFER",
            booking.Amount,
            booking.Id,
            DateTime.UtcNow);
    }

    private async Task<HttpResponseMessage> VerifyAsAsync(
        Booking booking,
        TestUser actor,
        string suffix = "")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/bookings/{booking.BookingCode}/verify-monnify{suffix}");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", actor.Token);
        return await _fixture.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostWebhookAsync(string body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/payments/monnify-webhook")
        {
            Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("monnify-signature", Sign(body));
        return _fixture.Client.SendAsync(request);
    }

    private static string CreateWebhookBody(string paymentReference) =>
        JsonSerializer.Serialize(new
        {
            eventType = "SUCCESSFUL_TRANSACTION",
            eventData = new
            {
                paymentReference,
                transactionReference = "UNTRUSTED-WEBHOOK-VALUE",
                paymentStatus = "PAID",
                amountPaid = 1
            }
        });

    private static string Sign(string body) =>
        Convert.ToHexString(HMACSHA512.HashData(
            Encoding.UTF8.GetBytes(WebhookSecret),
            Encoding.UTF8.GetBytes(body)));
}
