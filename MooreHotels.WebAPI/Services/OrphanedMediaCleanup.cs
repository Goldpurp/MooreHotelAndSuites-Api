using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Infrastructure.Persistence;
using Npgsql;

namespace MooreHotels.WebAPI.Services;

public sealed class OrphanedMediaCleanup
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrphanedMediaCleanup> _logger;

    public OrphanedMediaCleanup(
        IServiceScopeFactory scopeFactory,
        ILogger<OrphanedMediaCleanup> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task DeleteNowOrEnqueueAsync(
        string publicId,
        string sourceType,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var providerScope = _scopeFactory.CreateAsyncScope();
            var imageService = providerScope.ServiceProvider.GetRequiredService<IImageService>();
            if (await imageService.DeleteImageAsync(publicId)) return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Immediate cleanup failed for orphaned media {PublicId}; durable cleanup will be queued.",
                publicId);
        }

        try
        {
            await using var persistenceScope = _scopeFactory.CreateAsyncScope();
            var db = persistenceScope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
            if (await db.MediaDeletionJobs.AnyAsync(
                    job => job.PublicId == publicId,
                    cancellationToken))
            {
                return;
            }

            var outbox = persistenceScope.ServiceProvider.GetRequiredService<IMediaDeletionOutbox>();
            db.MediaDeletionJobs.Add(outbox.Create(publicId, sourceType, sourceId));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _logger.LogWarning(
                exception,
                "Orphaned media cleanup {PublicId} was already queued concurrently.",
                publicId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Orphaned media {PublicId} could not be deleted or durably queued.",
                publicId);
        }
    }
}
