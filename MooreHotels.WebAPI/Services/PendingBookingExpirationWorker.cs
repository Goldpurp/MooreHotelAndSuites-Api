using Microsoft.Extensions.Options;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.WebAPI.Configuration;

namespace MooreHotels.WebAPI.Services;

public sealed class PendingBookingExpirationWorker : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RuntimeSettings _runtime;
    private readonly ILogger<PendingBookingExpirationWorker> _logger;

    public PendingBookingExpirationWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<RuntimeSettings> runtime,
        ILogger<PendingBookingExpirationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _runtime = runtime.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_runtime.EnableBookingExpiration)
        {
            _logger.LogInformation("Automatic unpaid-booking expiration is disabled.");
            return;
        }

        await SweepOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepOnceAsync(stoppingToken);
        }
    }

    public async Task<int> SweepOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var total = 0;
        try
        {
            int expired;
            do
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
                var notifications =
                    await repository.CancelExpiredUnconfirmedWithNotificationsAsync(
                    DateTime.UtcNow,
                    BatchSize,
                    cancellationToken);
                expired = notifications.Count;
                total += expired;

                var emailService =
                    scope.ServiceProvider.GetRequiredService<IEmailService>();
                foreach (var notification in notifications)
                {
                    try
                    {
                        await emailService.SendCancellationNoticeAsync(
                            notification.GuestEmail,
                            notification.GuestName,
                            notification.BookingCode,
                            notification.RoomName,
                            notification.RoomCategory,
                            notification.CheckIn,
                            "Payment was not confirmed within one hour, so the room hold was released.");
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(
                            exception,
                            "Payment-expiry email failed for booking {BookingCode}.",
                            notification.BookingCode);
                    }
                }
            } while (expired == BatchSize && !cancellationToken.IsCancellationRequested);

            if (total > 0)
            {
                _logger.LogInformation(
                    "Expired {BookingCount} unpaid bookings after the one-hour confirmation window.",
                    total);
            }

            return total;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
            return total;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The unpaid-booking expiration sweep failed.");
            return total;
        }
    }
}
