using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace MooreHotels.WebAPI.Services;

public sealed record RealtimeAccessTicket(string Ticket, DateTimeOffset ExpiresAtUtc);

public sealed class RealtimeAccessTicketStore
{
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(1);
    private const int MaximumOutstandingTickets = 4096;
    private const int EncodedTicketLength = 43;

    private readonly ConcurrentDictionary<string, TicketEntry> _tickets = new();
    private readonly TimeProvider _timeProvider;

    public RealtimeAccessTicketStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public RealtimeAccessTicket Issue(string bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Length > 8192)
            throw new ArgumentException("A valid bearer token is required.", nameof(bearerToken));

        var now = _timeProvider.GetUtcNow();
        RemoveExpired(now);
        if (_tickets.Count >= MaximumOutstandingTickets)
            throw new InvalidOperationException("The realtime ticket capacity is temporarily exhausted.");

        while (true)
        {
            var ticket = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var expiresAtUtc = now.Add(TicketLifetime);
            if (_tickets.TryAdd(Hash(ticket), new TicketEntry(bearerToken, expiresAtUtc)))
                return new RealtimeAccessTicket(ticket, expiresAtUtc);
        }
    }

    public bool TryConsume(string? ticket, out string bearerToken)
    {
        bearerToken = string.Empty;
        if (ticket is null || ticket.Length != EncodedTicketLength ||
            ticket.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        if (!_tickets.TryRemove(Hash(ticket), out var entry) ||
            entry.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return false;
        }

        bearerToken = entry.BearerToken;
        return true;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var ticket in _tickets)
        {
            if (ticket.Value.ExpiresAtUtc <= now)
                _tickets.TryRemove(ticket.Key, out _);
        }
    }

    private static string Hash(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ticket)));

    private sealed record TicketEntry(string BearerToken, DateTimeOffset ExpiresAtUtc);
}
