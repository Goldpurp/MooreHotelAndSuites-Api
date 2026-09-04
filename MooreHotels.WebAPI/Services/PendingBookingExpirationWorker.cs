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
                expired = await repository.CancelExpiredUnconfirmedAsync(
                    DateTime.UtcNow,
                    BatchSize,
                    cancellationToken);
                total += expired;

            } while (expired == BatchSize && !cancellationToken.IsCancellationRequested);

            // Verification links are short-lived. Retain expired rows briefly
            // for troubleshooting, then remove them in bounded batches.
            int deletedVerifications;
            do
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
                deletedVerifications = await repository.DeleteExpiredEmailVerificationsAsync(
                    DateTime.UtcNow,
                    500,
                    cancellationToken);
            } while (deletedVerifications == 500 && !cancellationToken.IsCancellationRequested);

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
