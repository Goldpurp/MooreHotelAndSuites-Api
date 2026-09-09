namespace MooreHotels.Application.Interfaces;

public interface IApplicationTransaction
{
    Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default);
    Task ExecuteWithUserLockAsync(
        Guid userId,
        Func<Task> operation,
        CancellationToken cancellationToken = default);
    Task ExecuteWithAdminStatusLockAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default);
}
