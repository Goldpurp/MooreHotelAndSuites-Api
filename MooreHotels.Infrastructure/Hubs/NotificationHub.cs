using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace MooreHotels.Infrastructure.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    private readonly StaffConnectionRegistry _connections;

    public NotificationHub(StaffConnectionRegistry connections)
    {
        _connections = connections;
    }

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(
                Context.User?.FindFirstValue(ClaimTypes.NameIdentifier),
                out var userId) ||
            !await _connections.TryRegisterAsync(
                userId,
                Context.ConnectionId,
                Context.User?.FindFirstValue("security_stamp"),
                Groups,
                Context.ConnectionAborted))
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Guid.TryParse(
                Context.User?.FindFirstValue(ClaimTypes.NameIdentifier),
                out var userId))
        {
            await _connections.UnregisterAsync(userId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
