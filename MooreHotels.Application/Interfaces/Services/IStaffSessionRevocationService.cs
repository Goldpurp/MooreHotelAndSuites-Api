namespace MooreHotels.Application.Interfaces.Services;

public interface IStaffSessionRevocationService
{
    Task RevokeAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken = default);
}
