using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MooreHotels.Application.Common;
using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.WebAPI.Services;

public sealed class PrivacyRetentionWorker : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PrivacySettings _settings;
    private readonly ILogger<PrivacyRetentionWorker> _logger;

    public PrivacyRetentionWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PrivacySettings> settings,
        ILogger<PrivacyRetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.EnableRetentionWorker)
        {
            _logger.LogInformation("Guest privacy retention is disabled.");
            return;
        }

        await SweepOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepOnceAsync(stoppingToken);
        }
    }

    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.EnableRetentionWorker) return 0;

        var total = 0;
        try
        {
            int anonymized;
            do
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
                var strategy = db.Database.CreateExecutionStrategy();
                anonymized = await strategy.ExecuteAsync(async () =>
                {
                    var cutoffUtc = DateTime.UtcNow.AddDays(-_settings.GuestRetentionDays);
                    var accountCutoffUtc = DateTime.UtcNow.AddDays(
                        -_settings.InactiveAccountRetentionDays);
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    var guests = await db.Guests
                        .FromSqlInterpolated($$"""
                            SELECT g.*
                            FROM guests AS g
                            WHERE g."AnonymizedAtUtc" IS NULL
                              AND g."IsUnderLegalHold" = FALSE
                              AND g."LegalHoldPlacedAtUtc" IS NULL
                              AND NOT EXISTS (
                                  SELECT 1
                                  FROM users AS u
                                  WHERE u."GuestId" = g."Id"
                                    AND u."AnonymizedAtUtc" IS NULL
                                    AND (
                                        u."Role" <> 'Client'
                                        OR GREATEST(
                                            COALESCE(u."LastAuthenticatedAtUtc", u."CreatedAt"),
                                            COALESCE(u."StatusChangedAtUtc", u."CreatedAt")
                                        ) >= {{accountCutoffUtc}}
                                    ))
                              AND COALESCE((
                                  SELECT MAX(b."CheckOut") FROM bookings AS b WHERE b."GuestId" = g."Id"
                              ), g."CreatedAt") < {{cutoffUtc}}
                              AND NOT EXISTS (
                                  SELECT 1 FROM privacy_requests AS p
                                  WHERE p."GuestId" = g."Id"
                                    AND p."Status" IN ('Pending', 'InProgress'))
                            ORDER BY g."CreatedAt", g."Id"
                            FOR UPDATE SKIP LOCKED
                            LIMIT {{BatchSize}}
                            """)
                        .ToListAsync(cancellationToken);

                    var now = DateTime.UtcNow;
                    await db.EmailOutboxMessages
                        .Where(m => m.AttemptCount >= 12 && m.QuarantinedAtUtc == null)
                        .ExecuteUpdateAsync(
                            updates => updates
                                .SetProperty(m => m.Recipient, "redacted@delivery-failure.invalid")
                                .SetProperty(m => m.ProtectedPayload, "{}")
                                .SetProperty(m => m.QuarantinedAtUtc, now)
                                .SetProperty(m => m.DeliveryFailureMetadataJson, "{\"quarantinedBy\":\"retention_sweep\",\"reason\":\"exhausted_attempts\"}"),
                            cancellationToken);

                    if (guests.Count == 0)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        return 0;
                    }

                    var guestIds = guests.Select(guest => guest.Id).ToArray();
                    var guestEmails = guests.Select(guest => guest.Email).ToArray();
                    var bookingCodes = await db.Bookings
                        .Where(booking => guestIds.Contains(booking.GuestId))
                        .Select(booking => booking.BookingCode)
                        .ToArrayAsync(cancellationToken);
                    var bookingIds = await db.Bookings
                        .Where(booking => guestIds.Contains(booking.GuestId))
                        .Select(booking => booking.Id)
                        .ToArrayAsync(cancellationToken);
                    var accounts = await db.Users
                        .Where(account => account.GuestId != null &&
                                          guestIds.Contains(account.GuestId))
                        .ToListAsync(cancellationToken);
                    var accountIds = accounts.Select(account => account.Id).ToArray();
                    var mediaDeletionOutbox = scope.ServiceProvider
                        .GetRequiredService<IMediaDeletionOutbox>();
                    foreach (var guest in guests)
                    {
                        guest.FirstName = "Former";
                        guest.LastName = "Guest";
                        guest.Email = $"anonymized-{guest.Id.ToLowerInvariant()}@privacy.invalid";
                        guest.NormalizedEmail = guest.Email;
                        guest.Phone = "REDACTED";
                        guest.NormalizedPhone = string.Empty;
                        guest.EmailVerifiedAtUtc = null;
                        guest.PhoneVerifiedAtUtc = null;
                        guest.PreferencesJson = "{}";
                        guest.AvatarUrl = null;
                        guest.AnonymizedAtUtc = now;
                        db.AuditLogs.Add(new AuditLog
                        {
                            Id = Guid.NewGuid(),
                            ProfileId = BookingPaymentPolicy.SystemActorId,
                            Action = "GUEST_RETENTION_ANONYMIZED",
                            EntityType = "Guest",
                            EntityId = guest.Id,
                            NewDataJson = JsonSerializer.Serialize(new
                            {
                                guest.AnonymizedAtUtc,
                                RetentionDays = _settings.GuestRetentionDays
                            }),
                            CreatedAt = now
                        });
                    }

                    foreach (var account in accounts)
                    {
                        if (!string.IsNullOrWhiteSpace(account.AvatarPublicId))
                        {
                            db.MediaDeletionJobs.Add(mediaDeletionOutbox.Create(
                                account.AvatarPublicId,
                                "AccountRetention",
                                account.GuestId!));
                        }

                        var erasedEmail = $"retained-{account.Id:N}@privacy.invalid";
                        account.Name = "Former Guest";
                        account.Email = erasedEmail;
                        account.UserName = erasedEmail;
                        account.NormalizedEmail = erasedEmail.ToUpperInvariant();
                        account.NormalizedUserName = account.NormalizedEmail;
                        account.PhoneNumber = null;
                        account.EmailConfirmed = false;
                        account.PhoneNumberConfirmed = false;
                        account.AvatarUrl = null;
                        account.AvatarPublicId = null;
                        account.Status = ProfileStatus.Suspended;
                        account.StatusChangedAtUtc = now;
                        account.LastAuthenticatedAtUtc = null;
                        account.AnonymizedAtUtc = now;
                        account.PasswordHash = null;
                        account.TwoFactorEnabled = false;
                        account.LockoutEnd = DateTimeOffset.MaxValue;
                        account.SecurityStamp = Guid.NewGuid().ToString("N");
                    }

                    if (accountIds.Length > 0)
                    {
                        await db.UserTokens.Where(item => accountIds.Contains(item.UserId))
                            .ExecuteDeleteAsync(cancellationToken);
                        await db.UserLogins.Where(item => accountIds.Contains(item.UserId))
                            .ExecuteDeleteAsync(cancellationToken);
                        await db.UserClaims.Where(item => accountIds.Contains(item.UserId))
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    await db.Bookings
                        .Where(booking => guestIds.Contains(booking.GuestId))
                        .ExecuteUpdateAsync(
                            updates => updates
                                .SetProperty(b => b.Notes, (string?)null)
                                .SetProperty(b => b.RefundNotes, (string?)null)
                                .SetProperty(b => b.GuestAccessTokenHash, (string?)null),
                            cancellationToken);
                    await db.BookingAddOns
                        .Where(addOn => bookingIds.Contains(addOn.BookingId))
                        .ExecuteUpdateAsync(
                            updates => updates.SetProperty(a => a.Notes, (string?)null),
                            cancellationToken);
                    await db.VisitRecords
                        .Where(record => guestIds.Contains(record.GuestId))
                        .ExecuteUpdateAsync(
                            updates => updates.SetProperty(record => record.GuestName, "Former Guest"),
                            cancellationToken);
                    await db.GuestNotes
                        .Where(note => guestIds.Contains(note.GuestId))
                        .ExecuteUpdateAsync(
                            updates => updates
                                .SetProperty(note => note.Body, "Removed by retention policy")
                                .SetProperty(note => note.IsSensitive, false),
                            cancellationToken);
                    await db.PrivacyRequests
                        .Where(request => guestIds.Contains(request.GuestId))
                        .ExecuteUpdateAsync(
                            updates => updates
                                .SetProperty(request => request.Details, (string?)null)
                                .SetProperty(request => request.ResolutionNotes, "Removed by retention policy")
                                .SetProperty(
                                    request => request.IdentityVerificationReference,
                                    request => request.IdentityVerificationReference == null
                                        ? null
                                        : "REDACTED")
                                .SetProperty(
                                    request => request.FulfillmentEvidenceReference,
                                    request => request.FulfillmentEvidenceReference == null
                                        ? null
                                        : "REDACTED"),
                            cancellationToken);
                    await db.EmailOutboxMessages
                        .Where(message => message.DataSubjectGuestId != null &&
                                          guestIds.Contains(message.DataSubjectGuestId))
                        .ExecuteDeleteAsync(cancellationToken);
                    await db.BookingEmailVerifications
                        .Where(verification => guestEmails.Contains(verification.Email))
                        .ExecuteDeleteAsync(cancellationToken);
                    await db.Notifications
                        .Where(notification =>
                            notification.BookingCode != null &&
                            bookingCodes.Contains(notification.BookingCode))
                        .ExecuteUpdateAsync(
                            updates => updates
                                .SetProperty(notification => notification.Title, "Archived booking activity")
                                .SetProperty(notification => notification.Message, "Archived booking activity."),
                            cancellationToken);

                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return guests.Count;
                });
                total += anonymized;
            } while (anonymized == BatchSize && !cancellationToken.IsCancellationRequested);

            if (total > 0 && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Anonymized {GuestCount} expired guest records under the configured retention policy.",
                    total);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "The guest privacy-retention sweep failed with {ExceptionType}.",
                exception.GetType().Name);
        }

        return total;
    }
}
