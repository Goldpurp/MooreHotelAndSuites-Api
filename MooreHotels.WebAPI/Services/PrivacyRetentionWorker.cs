using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MooreHotels.Application.Common;
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
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    var guests = await db.Guests
                        .FromSqlInterpolated($$"""
                            SELECT g.*
                            FROM guests AS g
                            WHERE g."AnonymizedAtUtc" IS NULL
                              AND NOT EXISTS (
                                  SELECT 1 FROM users AS u WHERE u."GuestId" = g."Id")
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
                    if (guests.Count == 0)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        return 0;
                    }

                    var guestIds = guests.Select(guest => guest.Id).ToArray();
                    var bookingCodes = await db.Bookings
                        .Where(booking => guestIds.Contains(booking.GuestId))
                        .Select(booking => booking.BookingCode)
                        .ToArrayAsync(cancellationToken);
                    var now = DateTime.UtcNow;
                    foreach (var guest in guests)
                    {
                        guest.FirstName = "Former";
                        guest.LastName = "Guest";
                        guest.Email = $"anonymized-{guest.Id.ToLowerInvariant()}@privacy.invalid";
                        guest.Phone = "REDACTED";
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

                    await db.VisitRecords
                        .Where(record => guestIds.Contains(record.GuestId))
                        .ExecuteUpdateAsync(
                            updates => updates.SetProperty(record => record.GuestName, "Former Guest"),
                            cancellationToken);
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
                    "Anonymized {GuestCount} expired anonymous guest records under the configured retention policy.",
                    total);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The guest privacy-retention sweep failed.");
        }

        return total;
    }
}
