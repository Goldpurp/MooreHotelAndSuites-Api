using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.WebAPI.Services;

public sealed class EmailOutboxWorker : BackgroundService
{
    private const int BatchSize = 5;
    private const int MaximumAttempts = 12;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailOutboxWorker> _logger;

    public EmailOutboxWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EmailOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ProcessOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ProcessOnceAsync(stoppingToken);
    }

    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (messages, lockId) = await ClaimAsync(cancellationToken);
            await Task.WhenAll(messages.Select(message =>
                DeliverAsync(message, lockId, cancellationToken)));
            return messages.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "The transactional email outbox sweep failed with {ExceptionType}.",
                exception.GetType().Name);
            return 0;
        }
    }

    private async Task<(List<EmailOutboxMessage> Messages, Guid LockId)> ClaimAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var lockId = Guid.NewGuid();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var messages = await db.EmailOutboxMessages
                .FromSqlInterpolated(
                    $"""
                     SELECT * FROM email_outbox
                     WHERE "AttemptCount" < {MaximumAttempts}
                       AND "QuarantinedAtUtc" IS NULL
                       AND "NextAttemptAtUtc" <= {now}
                       AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {now})
                     ORDER BY "CreatedAtUtc"
                     LIMIT {BatchSize}
                     FOR UPDATE SKIP LOCKED
                     """)
                .ToListAsync(cancellationToken);

            foreach (var message in messages)
            {
                message.LockId = lockId;
                message.LockedUntilUtc = now.Add(LeaseDuration);
            }

            if (messages.Count > 0) await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (messages, lockId);
        });
    }

    private async Task DeliverAsync(
        EmailOutboxMessage message,
        Guid lockId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var deliveryScope = _scopeFactory.CreateAsyncScope())
            {
                var sender = deliveryScope.ServiceProvider.GetRequiredService<IEmailService>();
                var outbox = deliveryScope.ServiceProvider.GetRequiredService<IEmailOutbox>();
                var deliveryContext = deliveryScope.ServiceProvider.GetRequiredService<IEmailDeliveryContext>();
                deliveryContext.IdempotencyKey = message.Id;
                await DispatchAsync(sender, outbox, message);
            }

            await using var successScope = _scopeFactory.CreateAsyncScope();
            var db = successScope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
            var persisted = await db.EmailOutboxMessages.SingleOrDefaultAsync(
                item => item.Id == message.Id && item.LockId == lockId,
                cancellationToken);
            if (persisted is not null)
            {
                db.EmailOutboxMessages.Remove(persisted);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(message.Id, lockId, exception, cancellationToken);
        }
    }

    private async Task MarkFailedAsync(
        Guid messageId,
        Guid lockId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var message = await db.EmailOutboxMessages.SingleOrDefaultAsync(
            item => item.Id == messageId && item.LockId == lockId,
            cancellationToken);
        if (message is null) return;

        message.AttemptCount++;
        message.LockId = null;
        message.LockedUntilUtc = null;
        message.LastErrorCode = exception.GetType().Name[..Math.Min(80, exception.GetType().Name.Length)];

        if (message.AttemptCount >= MaximumAttempts)
        {
            message.QuarantinedAtUtc = DateTime.UtcNow;
            var failureMetadata = new
            {
                QuarantinedAtUtc = message.QuarantinedAtUtc.Value,
                Attempts = message.AttemptCount,
                LastErrorCode = message.LastErrorCode
            };
            message.DeliveryFailureMetadataJson = JsonSerializer.Serialize(failureMetadata);
            message.Recipient = "quarantined-failure@delivery-failure.invalid";
            message.ProtectedPayload = "{}";

            _logger.LogError(
                "Transactional email {EmailId} permanently exhausted after {Attempt} attempts with {ErrorCode} and has been quarantined with recipient and payload scrubbed.",
                message.Id,
                message.AttemptCount,
                message.LastErrorCode);
        }
        else
        {
            var delayMinutes = Math.Min(360, Math.Pow(2, Math.Min(message.AttemptCount, 8)));
            message.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(delayMinutes);

            _logger.LogWarning(
                "Transactional email {EmailId} failed on attempt {Attempt}; a retry was scheduled.",
                message.Id,
                message.AttemptCount);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Task DispatchAsync(
        IEmailService sender,
        IEmailOutbox outbox,
        EmailOutboxMessage message) =>
        message.Template switch
        {
            TransactionalEmailTemplates.BookingConfirmation =>
                SendBookingConfirmationAsync(sender, outbox, message),
            TransactionalEmailTemplates.BookingAccessLink =>
                SendBookingAccessLinkAsync(sender, outbox, message),
            TransactionalEmailTemplates.BookingEmailVerification =>
                SendBookingEmailVerificationAsync(sender, outbox, message),
            TransactionalEmailTemplates.AdminNewBooking =>
                SendAdminNewBookingAsync(sender, outbox, message),
            TransactionalEmailTemplates.Cancellation =>
                SendCancellationAsync(sender, outbox, message),
            TransactionalEmailTemplates.PaymentSuccess =>
                SendPaymentSuccessAsync(sender, outbox, message),
            TransactionalEmailTemplates.CheckOutThankYou =>
                SendCheckOutThankYouAsync(sender, outbox, message),
            TransactionalEmailTemplates.AdminRefund =>
                SendAdminRefundAsync(sender, outbox, message),
            TransactionalEmailTemplates.RefundCompleted =>
                SendRefundCompletedAsync(sender, outbox, message),
            TransactionalEmailTemplates.EmailVerification =>
                SendEmailVerificationAsync(sender, outbox, message),
            TransactionalEmailTemplates.PasswordReset =>
                SendPasswordResetAsync(sender, outbox, message),
            TransactionalEmailTemplates.StaffWelcome =>
                SendStaffWelcomeAsync(sender, outbox, message),
            TransactionalEmailTemplates.AccountSuspended =>
                SendAccountSuspendedAsync(sender, outbox, message),
            TransactionalEmailTemplates.AccountActivated =>
                SendAccountActivatedAsync(sender, outbox, message),
            TransactionalEmailTemplates.BookingAmendmentConfirmation =>
                SendBookingAmendmentConfirmationAsync(sender, outbox, message),
            TransactionalEmailTemplates.FolioReceipt =>
                SendFolioReceiptAsync(sender, outbox, message),
            _ => throw new InvalidOperationException("Unknown transactional email template.")
        };

    private static Task SendBookingAmendmentConfirmationAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var payload = outbox.ReadPayload<BookingAmendmentConfirmationEmail>(message);
        return sender.SendBookingAmendmentConfirmationAsync(message.Recipient, payload);
    }

    private static Task SendFolioReceiptAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var payload = outbox.ReadPayload<FolioReceiptEmail>(message);
        return sender.SendFolioReceiptAsync(message.Recipient, payload);
    }

    private static Task SendBookingConfirmationAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var p = outbox.ReadPayload<BookingConfirmationEmail>(message);
        return sender.SendBookingConfirmationAsync(message.Recipient, p.GuestName, p.BookingCode,
            p.RoomName, p.RoomCategory, p.Capacity, p.AdultCount, p.ChildCount, p.CheckIn, p.CheckOut, p.Nights,
            p.TotalAmount, p.ManageBookingUrl);
    }

    private static Task SendBookingAccessLinkAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var p = outbox.ReadPayload<BookingAccessLinkEmail>(message);
        return sender.SendBookingAccessLinkAsync(message.Recipient, p.GuestName, p.BookingCode, p.ManageBookingUrl);
    }

    private static Task SendBookingEmailVerificationAsync(
        IEmailService sender,
        IEmailOutbox outbox,
        EmailOutboxMessage message)
    {
        var payload = outbox.ReadPayload<BookingEmailVerificationEmail>(message);
        return sender.SendBookingEmailVerificationAsync(
            message.Recipient,
            payload.VerificationLink);
    }

    private static Task SendAdminNewBookingAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var p = outbox.ReadPayload<AdminNewBookingEmail>(message);
        return sender.SendAdminNewBookingAlertAsync(message.Recipient, p.GuestName, p.BookingCode,
            p.RoomName, p.RoomCategory, p.Capacity, p.AdultCount, p.ChildCount, p.CheckIn, p.CheckOut, p.Nights,
            p.TotalAmount, p.GuestEmail, p.GuestPhone);
    }

    private static Task SendCancellationAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var p = outbox.ReadPayload<CancellationEmail>(message);
        return sender.SendCancellationNoticeAsync(message.Recipient, p.GuestName, p.BookingCode,
            p.RoomName, p.RoomCategory, p.CheckIn, p.Reason);
    }

    private static Task SendPaymentSuccessAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var p = outbox.ReadPayload<PaymentSuccessEmail>(message);
        return sender.SendPaymentSuccessAsync(message.Recipient, p.GuestName, p.BookingCode,
            p.RoomName, p.Amount, p.Reference);
    }

    private static Task SendCheckOutThankYouAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var p = outbox.ReadPayload<CheckOutThankYouEmail>(message);
        return sender.SendCheckOutThankYouAsync(message.Recipient, p.GuestName, p.BookingCode, p.RoomName);
    }

    private static Task SendAdminRefundAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var p = outbox.ReadPayload<AdminRefundEmail>(message);
        return sender.SendAdminRefundAlertAsync(message.Recipient, p.GuestName, p.BookingCode,
            p.RoomName, p.Amount);
    }

    private static Task SendRefundCompletedAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var p = outbox.ReadPayload<RefundCompletedEmail>(message);
        return sender.SendRefundCompletionNoticeAsync(message.Recipient, p.GuestName, p.BookingCode,
            p.RoomName, p.Amount, p.Reference);
    }

    private static Task SendEmailVerificationAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var payload = outbox.ReadPayload<EmailVerificationEmail>(message);
        return sender.SendEmailVerificationAsync(message.Recipient, payload.Name, payload.Link);
    }

    private static Task SendPasswordResetAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var payload = outbox.ReadPayload<PasswordResetEmail>(message);
        return sender.SendPasswordResetAsync(message.Recipient, payload.Name, payload.Link);
    }

    private static Task SendStaffWelcomeAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var payload = outbox.ReadPayload<StaffWelcomeEmail>(message);
        return sender.SendStaffWelcomeEmailAsync(
            message.Recipient,
            payload.Name,
            payload.SetupLink,
            payload.Role);
    }

    private static Task SendAccountSuspendedAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var payload = outbox.ReadPayload<AccountStatusEmail>(message);
        return sender.SendAccountSuspendedAsync(message.Recipient, payload.Name);
    }

    private static Task SendAccountActivatedAsync(IEmailService sender, IEmailOutbox outbox, EmailOutboxMessage message)
    {
        var payload = outbox.ReadPayload<AccountStatusEmail>(message);
        return sender.SendAccountActivatedAsync(message.Recipient, payload.Name);
    }
}
