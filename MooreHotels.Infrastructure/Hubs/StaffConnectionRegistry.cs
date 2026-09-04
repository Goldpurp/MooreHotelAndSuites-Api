using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Hubs;

public sealed class StaffConnectionRegistry : IStaffSessionRevocationService
{
    public const string StaffGroup = "StaffGroup";

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _connections = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userGates = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<StaffConnectionRegistry> _logger;

    public StaffConnectionRegistry(
        IServiceScopeFactory scopeFactory,
        IHubContext<NotificationHub> hubContext,
        ILogger<StaffConnectionRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<bool> TryRegisterAsync(
        Guid userId,
        string connectionId,
        string? tokenSecurityStamp,
        IGroupManager groups,
        CancellationToken cancellationToken = default)
    {
        var gate = _userGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
            var user = await db.Users
                .AsNoTracking()
                .Where(item => item.Id == userId)
                .Select(item => new
                {
                    item.Role,
                    item.Department,
                    item.Status,
                    item.SecurityStamp
                })
                .SingleOrDefaultAsync(cancellationToken);

            var isStaff = user?.Role is UserRole.Admin or UserRole.Manager ||
                          user?.Role == UserRole.Staff &&
                          new[] { "Reception", "FrontDesk", "Concierge" }
                              .Contains(user.Department, StringComparer.OrdinalIgnoreCase);
            var isAuthorized = user is not null &&
                               user.Status == ProfileStatus.Active &&
                               isStaff &&
                               !string.IsNullOrWhiteSpace(tokenSecurityStamp) &&
                               string.Equals(
                                   tokenSecurityStamp,
                                   user.SecurityStamp,
                                   StringComparison.Ordinal);
            if (!isAuthorized) return false;

            await groups.AddToGroupAsync(connectionId, StaffGroup, cancellationToken);
            _connections
                .GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
                .TryAdd(connectionId, 0);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UnregisterAsync(
        Guid userId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var gate = _userGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_connections.TryGetValue(userId, out var connections))
            {
                connections.TryRemove(connectionId, out _);
                if (connections.IsEmpty) _connections.TryRemove(userId, out _);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RevokeAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var gate = _userGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!_connections.TryRemove(userId, out var connections) || connections.IsEmpty)
                return;

            var connectionIds = connections.Keys.ToArray();
            try
            {
                await _hubContext.Clients.Clients(connectionIds).SendAsync(
                    "AccessRevoked",
                    new { reason },
                    cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not notify every SignalR connection that access was revoked for user {UserId}.",
                    userId);
            }

            foreach (var connectionId in connectionIds)
            {
                try
                {
                    await _hubContext.Groups.RemoveFromGroupAsync(
                        connectionId,
                        StaffGroup,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not remove SignalR connection {ConnectionId} for revoked user {UserId}.",
                        connectionId,
                        userId);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public int GetActiveConnectionCount(Guid userId) =>
        _connections.TryGetValue(userId, out var connections)
            ? connections.Count
            : 0;
}
