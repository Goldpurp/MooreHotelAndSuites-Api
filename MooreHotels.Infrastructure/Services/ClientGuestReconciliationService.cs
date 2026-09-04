using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Services;

public sealed class ClientGuestReconciliationService : IClientGuestReconciliationService
{
    private readonly MooreHotelsDbContext _db;

    public ClientGuestReconciliationService(MooreHotelsDbContext db) => _db = db;

    public async Task<PagedResult<ClientGuestLinkIssueDto>> GetIssuesAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Users
            .AsNoTracking()
            .Where(user => user.Role == UserRole.Client &&
                           (user.GuestId == null ||
                            !_db.Guests.Any(guest => guest.Id == user.GuestId)));
        var totalCount = await query.CountAsync(cancellationToken);
        var users = await query
            .OrderBy(user => user.CreatedAt)
            .ThenBy(user => user.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(user => new
            {
                user.Id,
                user.Name,
                Email = user.Email ?? string.Empty,
                user.GuestId
            })
            .ToListAsync(cancellationToken);

        var emails = users
            .Select(user => user.Email.Trim().ToLowerInvariant())
            .Where(email => email.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidates = await _db.Guests
            .AsNoTracking()
            .Where(guest => emails.Contains(guest.Email))
            .OrderBy(guest => guest.CreatedAt)
            .ToListAsync(cancellationToken);

        var items = users.Select(user => new ClientGuestLinkIssueDto(
                user.Id,
                user.Name,
                user.Email,
                user.GuestId,
                string.IsNullOrWhiteSpace(user.GuestId)
                    ? "MissingGuestLink"
                    : "LinkedGuestProfileMissing",
                candidates
                    .Where(guest => string.Equals(
                        guest.Email,
                        user.Email,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(guest => new GuestLinkCandidateDto(
                        guest.Id,
                        $"{guest.FirstName} {guest.LastName}",
                        guest.Email,
                        guest.Phone))
                    .ToList()))
            .ToList();

        return PagedResult<ClientGuestLinkIssueDto>.Create(
            items,
            totalCount,
            pageNumber,
            pageSize);
    }

    public async Task<ClientGuestReconciliationResult> ReconcileAsync(
        Guid userId,
        ReconcileClientGuestRequest request,
        Guid actingUserId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var evidenceType = request.EvidenceType.Trim();
        var guestId = request.GuestId.Trim().ToUpperInvariant();
        var reason = request.Reason.Trim();
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            var actingUser = await _db.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE \"Id\" = {actingUserId} FOR SHARE")
                .SingleOrDefaultAsync(cancellationToken);
            if (actingUser is null ||
                actingUser.Role != UserRole.Admin ||
                actingUser.Status != ProfileStatus.Active)
            {
                throw new UnauthorizedAccessException(
                    "Only an active administrator can reconcile client and guest identities.");
            }

            var user = await _db.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (user is null || user.Role != UserRole.Client)
                throw new NotFoundException("Client account not found.");

            if (!string.IsNullOrWhiteSpace(user.GuestId))
            {
                var currentGuestExists = await _db.Guests.AnyAsync(
                    current => current.Id == user.GuestId,
                    cancellationToken);
                if (currentGuestExists)
                {
                    if (string.Equals(user.GuestId, guestId, StringComparison.Ordinal))
                    {
                        return new ClientGuestReconciliationResult(
                            user.Id,
                            guestId,
                            evidenceType,
                            DateTime.UtcNow);
                    }

                    throw new BadRequestException(
                        "This client account already has a valid guest-profile link.");
                }
            }

            var guest = await _db.Guests
                .FromSqlInterpolated(
                    $"SELECT * FROM guests WHERE \"Id\" = {guestId} FOR SHARE")
                .SingleOrDefaultAsync(cancellationToken);
            if (guest is null) throw new NotFoundException("Guest profile not found.");

            var alreadyLinked = await _db.Users.AnyAsync(
                candidate => candidate.Id != userId && candidate.GuestId == guestId,
                cancellationToken);
            if (alreadyLinked)
                throw new BadRequestException("That guest profile is already linked to another account.");

            var emailMatches = string.Equals(
                user.Email?.Trim(),
                guest.Email.Trim(),
                StringComparison.OrdinalIgnoreCase);
            var evidenceMatches = evidenceType switch
            {
                "VerifiedEmail" => emailMatches,
                "VerifiedPhone" => PhoneNumbersMatch(user.PhoneNumber, guest.Phone),
                "GovernmentId" or "InPersonIdentityCheck" => true,
                _ => false
            };
            if (!evidenceMatches)
            {
                throw new BadRequestException(
                    "The selected identity evidence does not match this client and guest profile.");
            }

            var previousGuestId = user.GuestId;
            var reconciledAtUtc = DateTime.UtcNow;
            user.GuestId = guest.Id;
            user.SecurityStamp = Guid.NewGuid().ToString();
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ProfileId = actingUserId,
                Action = "CLIENT_GUEST_RECONCILED",
                EntityType = "User",
                EntityId = user.Id.ToString(),
                OldDataJson = JsonSerializer.Serialize(new { GuestId = previousGuestId }),
                NewDataJson = JsonSerializer.Serialize(new
                {
                    GuestId = guest.Id,
                    EvidenceType = evidenceType,
                    Reason = reason,
                    RequestId = requestId,
                    ReconciledAtUtc = reconciledAtUtc
                }),
                CreatedAt = reconciledAtUtc
            });

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ClientGuestReconciliationResult(
                user.Id,
                guest.Id,
                evidenceType,
                reconciledAtUtc);
        });
    }

    private static bool PhoneNumbersMatch(string? userPhone, string? guestPhone)
    {
        if (string.IsNullOrWhiteSpace(userPhone) || string.IsNullOrWhiteSpace(guestPhone))
            return false;

        var normalizedUserPhone = new string(userPhone.Where(char.IsDigit).ToArray());
        var normalizedGuestPhone = new string(guestPhone.Where(char.IsDigit).ToArray());
        return normalizedUserPhone.Length >= 7 &&
               string.Equals(
                   normalizedUserPhone,
                   normalizedGuestPhone,
                   StringComparison.Ordinal);
    }
}
