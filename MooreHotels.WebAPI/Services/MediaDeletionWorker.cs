using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Configuration;

namespace MooreHotels.WebAPI.Services;

public sealed class MediaDeletionWorker : BackgroundService
{
    public const int MaximumAttempts = 12;
    private const int BatchSize = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RuntimeSettings _runtime;
    private readonly ILogger<MediaDeletionWorker> _logger;

    public MediaDeletionWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<RuntimeSettings> runtime,
        ILogger<MediaDeletionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _runtime = runtime.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_runtime.EnableMediaDeletion) return;

        await ProcessOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ProcessOnceAsync(stoppingToken);
    }

    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (jobs, lockId) = await ClaimAsync(cancellationToken);
            await Task.WhenAll(jobs.Select(job => DeleteAsync(job, lockId, cancellationToken)));
            return jobs.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The media-deletion outbox sweep failed.");
            return 0;
        }
    }

    private async Task<(List<MediaDeletionJob> Jobs, Guid LockId)> ClaimAsync(
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
            var jobs = await db.MediaDeletionJobs
                .FromSqlInterpolated(
                    $"""
                     SELECT * FROM media_deletion_outbox
                     WHERE "AttemptCount" < {MaximumAttempts}
                       AND "NextAttemptAtUtc" <= {now}
                       AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {now})
                     ORDER BY "CreatedAtUtc"
                     LIMIT {BatchSize}
                     FOR UPDATE SKIP LOCKED
                     """)
                .ToListAsync(cancellationToken);

            foreach (var job in jobs)
            {
                job.LockId = lockId;
                job.LockedUntilUtc = now.Add(LeaseDuration);
            }

            if (jobs.Count > 0) await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (jobs, lockId);
        });
    }

    private async Task DeleteAsync(
        MediaDeletionJob job,
        Guid lockId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var providerScope = _scopeFactory.CreateAsyncScope())
            {
                var imageService = providerScope.ServiceProvider.GetRequiredService<IImageService>();
                if (!await imageService.DeleteImageAsync(job.PublicId))
                    throw new InvalidOperationException("The image provider did not confirm deletion.");
            }

            await using var successScope = _scopeFactory.CreateAsyncScope();
            var db = successScope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
            var persisted = await db.MediaDeletionJobs.SingleOrDefaultAsync(
                item => item.Id == job.Id && item.LockId == lockId,
                cancellationToken);
            if (persisted is not null)
            {
                db.MediaDeletionJobs.Remove(persisted);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(job.Id, lockId, exception, cancellationToken);
        }
    }

    private async Task MarkFailedAsync(
        Guid jobId,
        Guid lockId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var job = await db.MediaDeletionJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.LockId == lockId,
            cancellationToken);
        if (job is null) return;

        job.AttemptCount++;
        job.LockId = null;
        job.LockedUntilUtc = null;
        job.LastErrorCode = exception.GetType().Name[..Math.Min(80, exception.GetType().Name.Length)];
        var delayMinutes = Math.Min(360, Math.Pow(2, Math.Min(job.AttemptCount, 8)));
        job.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Media deletion {MediaDeletionId} failed on attempt {Attempt}; a retry was scheduled.",
            job.Id,
            job.AttemptCount);
    }
}
