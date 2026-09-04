using Microsoft.EntityFrameworkCore;

namespace MooreHotels.Infrastructure.Persistence;

public sealed class ApplicationTransaction : MooreHotels.Application.Interfaces.IApplicationTransaction
{
    private readonly MooreHotelsDbContext _db;

    public ApplicationTransaction(MooreHotelsDbContext db) => _db = db;

    public async Task ExecuteAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public Task ExecuteWithUserLockAsync(
        Guid userId,
        Func<Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var user = await _db.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (user is null)
            {
                throw new InvalidOperationException("The user no longer exists.");
            }

            await operation();
        }, cancellationToken);
}
