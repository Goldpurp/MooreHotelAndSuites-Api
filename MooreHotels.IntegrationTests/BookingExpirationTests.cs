using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class BookingExpirationTests
{
    private readonly ManualTransferTestFixture _fixture;

    public BookingExpirationTests(ManualTransferTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(PaymentMethod.DirectTransfer, PaymentStatus.AwaitingVerification)]
    [InlineData(PaymentMethod.Monnify, PaymentStatus.Unpaid)]
    public async Task One_hour_old_unconfirmed_booking_is_cancelled_and_audited(
        PaymentMethod paymentMethod,
        PaymentStatus paymentStatus)
    {
        var now = DateTime.UtcNow;
        var booking = await _fixture.CreateBookingAsync(
            paymentMethod,
            paymentStatus,
            createdAtUtc: now.Subtract(BookingPaymentPolicy.ConfirmationWindow));

        var expired = await ExpireAsync(now);

        Assert.Equal(1, expired);
        var state = await _fixture.WithDbAsync(async db => new
        {
            Booking = await db.Bookings.AsNoTracking().SingleAsync(item => item.Id == booking.Id),
            Audit = await db.AuditLogs.AsNoTracking().SingleAsync(log =>
                log.Action == "UNCONFIRMED_BOOKING_EXPIRED" &&
                log.EntityId == booking.Id.ToString())
        });
        Assert.Equal(BookingStatus.Cancelled, state.Booking.Status);
        Assert.Equal(paymentStatus, state.Booking.PaymentStatus);
        Assert.Contains(
            "Payment was not confirmed within one hour",
            state.Booking.StatusHistoryJson ?? string.Empty);
        Assert.Equal(BookingPaymentPolicy.SystemActorId, state.Audit.ProfileId);
        Assert.DoesNotContain("password", state.Audit.NewDataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", state.Audit.NewDataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fresh_or_paid_booking_is_never_expired()
    {
        var now = DateTime.UtcNow;
        var fresh = await _fixture.CreateBookingAsync(createdAtUtc: now.AddMinutes(-30));
        var paid = await _fixture.CreateBookingAsync(
            paymentStatus: PaymentStatus.Paid,
            bookingStatus: BookingStatus.Confirmed,
            createdAtUtc: now.AddHours(-3));

        var expired = await ExpireAsync(now);

        Assert.Equal(0, expired);
        var statuses = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .Where(item => item.Id == fresh.Id || item.Id == paid.Id)
            .ToDictionaryAsync(item => item.Id, item => new { item.Status, item.PaymentStatus }));
        Assert.Equal(BookingStatus.Pending, statuses[fresh.Id].Status);
        Assert.Equal(PaymentStatus.AwaitingVerification, statuses[fresh.Id].PaymentStatus);
        Assert.Equal(BookingStatus.Confirmed, statuses[paid.Id].Status);
        Assert.Equal(PaymentStatus.Paid, statuses[paid.Id].PaymentStatus);
    }

    [Fact]
    public async Task Expired_hold_stops_blocking_immediately_and_another_booking_can_take_the_dates()
    {
        var now = DateTime.UtcNow;
        var expiredBooking = await _fixture.CreateBookingAsync(createdAtUtc: now.AddMinutes(-61));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        Assert.False(await repository.IsRoomBookedAsync(
            expiredBooking.RoomId,
            expiredBooking.CheckIn,
            expiredBooking.CheckOut));

        var replacement = new Booking
        {
            Id = Guid.NewGuid(),
            BookingCode = $"MHS-{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            RoomId = expiredBooking.RoomId,
            GuestId = expiredBooking.GuestId,
            CheckIn = expiredBooking.CheckIn,
            CheckOut = expiredBooking.CheckOut,
            Status = BookingStatus.Pending,
            Amount = expiredBooking.Amount,
            PaymentStatus = PaymentStatus.AwaitingVerification,
            PaymentMethod = PaymentMethod.DirectTransfer,
            StatusHistoryJson = "[]",
            CreatedAt = now
        };

        await repository.AddAsync(replacement);

        var stored = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == replacement.Id));
        Assert.Equal(BookingStatus.Pending, stored.Status);
        await repository.CancelExpiredUnconfirmedAsync(now);
    }

    [Fact]
    public async Task Concurrent_sweeps_cancel_once_and_create_one_audit_record()
    {
        var now = DateTime.UtcNow;
        var booking = await _fixture.CreateBookingAsync(createdAtUtc: now.AddMinutes(-61));

        var results = await Task.WhenAll(ExpireAsync(now), ExpireAsync(now));

        Assert.Equal(1, results.Sum());
        var auditCount = await _fixture.WithDbAsync(db => db.AuditLogs.CountAsync(log =>
            log.Action == "UNCONFIRMED_BOOKING_EXPIRED" &&
            log.EntityId == booking.Id.ToString()));
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task Expiration_rolls_back_if_the_audit_record_cannot_be_saved()
    {
        var now = DateTime.UtcNow;
        var booking = await _fixture.CreateBookingAsync(createdAtUtc: now.AddMinutes(-61));
        await _fixture.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION fail_booking_expiration_audit() RETURNS trigger AS $$
                BEGIN
                    IF NEW."Action" = 'UNCONFIRMED_BOOKING_EXPIRED' THEN
                        RAISE EXCEPTION 'forced booking expiration audit failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_booking_expiration_audit_trigger
                BEFORE INSERT ON audit_logs
                FOR EACH ROW EXECUTE FUNCTION fail_booking_expiration_audit();
                """);
            return true;
        });

        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => ExpireAsync(now));
            var stored = await _fixture.WithDbAsync(db => db.Bookings
                .AsNoTracking()
                .SingleAsync(item => item.Id == booking.Id));
            Assert.Equal(BookingStatus.Pending, stored.Status);
            Assert.Equal(0, await _fixture.WithDbAsync(db => db.AuditLogs.CountAsync(log =>
                log.EntityId == booking.Id.ToString())));
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER IF EXISTS fail_booking_expiration_audit_trigger ON audit_logs;
                    DROP FUNCTION IF EXISTS fail_booking_expiration_audit();
                    """);
                return true;
            });
        }

        await ExpireAsync(now);
    }

    [Fact]
    public async Task Pending_booking_response_contains_the_exact_payment_deadline()
    {
        var booking = await _fixture.CreateBookingAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IBookingService>();

        var result = await service.GetBookingByCodeAsync(booking.BookingCode);

        Assert.NotNull(result);
        Assert.NotNull(result.PaymentExpiresAtUtc);
        Assert.True(
            (BookingPaymentPolicy.GetConfirmationDeadlineUtc(booking.CreatedAt) -
             result.PaymentExpiresAtUtc.Value).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Background_expiration_worker_is_registered()
    {
        var workers = _fixture.Services.GetServices<IHostedService>();
        Assert.Contains(workers, worker => worker is PendingBookingExpirationWorker);
    }

    [Fact]
    public async Task New_booking_codes_are_random_unique_and_database_allocated()
    {
        async Task<string> GenerateAsync()
        {
            await using var scope = _fixture.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
            return await repository.GenerateBookingCodeAsync();
        }

        var codes = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(_ => GenerateAsync()));

        Assert.All(codes, code => Assert.Matches("^MHS[0-9]{6}$", code));
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());

        // Sequential public identifiers disclose booking volume. At least one
        // adjacent generated pair must not be a consecutive numeric value.
        var values = codes.Select(code => int.Parse(code[3..])).ToArray();
        Assert.Contains(
            values.Zip(values.Skip(1)),
            pair => Math.Abs(pair.First - pair.Second) != 1);

        var reservedCount = await _fixture.WithDbAsync(db =>
        {
            return db.BookingCodeAllocations
                .CountAsync(allocation => codes.Contains(allocation.Code));
        });
        Assert.Equal(codes.Length, reservedCount);
    }

    [Fact]
    public async Task Late_payment_cannot_restore_an_expired_booking()
    {
        var now = DateTime.UtcNow;
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.Monnify,
            PaymentStatus.Unpaid,
            BookingStatus.Pending,
            now.AddMinutes(-61));
        var paymentReference = $"LATE-{Guid.NewGuid():N}";
        var providerReference = $"MNFY|TEST|{Guid.NewGuid():N}";
        string guestEmail = string.Empty;
        await _fixture.WithDbAsync(async db =>
        {
            var stored = await db.Bookings.SingleAsync(item => item.Id == booking.Id);
            stored.TransactionReference = paymentReference;
            stored.PaymentProviderReference = providerReference;
            guestEmail = await db.Guests
                .Where(item => item.Id == stored.GuestId)
                .Select(item => item.Email)
                .SingleAsync();
            await db.SaveChangesAsync();
            return true;
        });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider
            .GetRequiredService<IMonnifyPaymentProcessor>();
        var outcome = await processor.ApplyVerifiedPaymentAsync(
            booking.Id,
            new MonnifyVerificationResult(
                paymentReference,
                providerReference,
                "PAID",
                booking.Amount,
                "NGN",
                booking.BookingCode,
                guestEmail,
                "Transfer Tester",
                100m,
                booking.Amount - 100m,
                "ACCOUNT_TRANSFER",
                booking.Amount,
                booking.Id,
                DateTime.UtcNow),
            "Webhook",
            null,
            "expiration-test");

        Assert.Equal(
            MonnifyPaymentOutcomeKind.PaidAfterExpiry,
            outcome.Kind);
        var state = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == booking.Id));
        Assert.Equal(BookingStatus.Pending, state.Status);
        Assert.Equal(PaymentStatus.Unpaid, state.PaymentStatus);
        await ExpireAsync(now);
    }

    [Fact]
    public async Task Late_manual_transfer_confirmation_is_rejected_before_the_sweeper_runs()
    {
        var booking = await _fixture.CreateBookingAsync(
            PaymentMethod.DirectTransfer,
            PaymentStatus.AwaitingVerification,
            BookingStatus.Pending,
            DateTime.UtcNow.AddMinutes(-61));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/bookings/{booking.BookingCode}/confirm-transfer")
        {
            Content = JsonContent.Create(new
            {
                confirmationText = "ACCEPT",
                confirmationMethod = "TypedAcknowledgement",
                transactionReference = "IGNORED-LEGACY-REFERENCE"
            })
        };
        request.Headers.Authorization = new(
            "Bearer",
            _fixture.Admin.Token);

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var state = await _fixture.WithDbAsync(db => db.Bookings
            .AsNoTracking()
            .SingleAsync(item => item.Id == booking.Id));
        Assert.Equal(BookingStatus.Pending, state.Status);
        Assert.Equal(PaymentStatus.AwaitingVerification, state.PaymentStatus);
        await ExpireAsync(DateTime.UtcNow);
    }

    private async Task<int> ExpireAsync(DateTime utcNow)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        return await repository.CancelExpiredUnconfirmedAsync(utcNow);
    }
}
