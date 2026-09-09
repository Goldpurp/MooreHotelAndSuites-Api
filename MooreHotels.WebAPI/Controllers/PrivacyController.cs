using System.Security.Claims;
using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Extensions;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/privacy")]
public sealed class PrivacyController : ControllerBase
{
    private readonly MooreHotelsDbContext _db;
    private readonly PrivacySettings _settings;
    private readonly PrivacyDataService _privacyData;

    public PrivacyController(
        MooreHotelsDbContext db,
        IOptions<PrivacySettings> settings,
        PrivacyDataService privacyData)
    {
        _db = db;
        _settings = settings.Value;
        _privacyData = privacyData;
    }

    [HttpGet("policies/current")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicReadRateLimitPolicy)]
    public IActionResult GetCurrentPolicies() => Ok(new PrivacyPolicyDto(
        _settings.CurrentPrivacyPolicyVersion,
        _settings.CurrentBookingTermsVersion,
        _settings.PrivacyPolicyUrl,
        _settings.BookingTermsUrl,
        _settings.RequirePolicyAcceptance));

    [HttpGet("export")]
    [Authorize(Roles = "Client")]
    public async Task<IActionResult> ExportMyData(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var user = await _db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.GuestId))
            return NotFound(new { Message = "No linked guest profile is available for export." });

        return Ok(await _privacyData.BuildExportAsync(
            user.GuestId,
            DateTime.UtcNow,
            cancellationToken));
    }

    [HttpPost("requests")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
    public async Task<IActionResult> CreateRequest(
        [FromBody] CreatePrivacyRequestRequest request,
        [FromHeader(Name = "X-Booking-Access-Token")] string? accessToken,
        CancellationToken cancellationToken)
    {
        string? guestId = null;
        Guid? requestedByUserId = null;
        if (TryGetUserId(out var userId))
        {
            var account = await _db.Users
                .AsNoTracking()
                .Where(item => item.Id == userId && item.Role == UserRole.Client)
                .Select(item => new { item.Id, item.GuestId })
                .SingleOrDefaultAsync(cancellationToken);
            if (account is null || string.IsNullOrWhiteSpace(account.GuestId))
                return Forbid();

            guestId = account.GuestId;
            requestedByUserId = account.Id;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.BookingCode) ||
                string.IsNullOrWhiteSpace(accessToken))
            {
                return BadRequest(new
                {
                    Message = "Sign in or use a secure booking link to submit a privacy request."
                });
            }

            var normalizedBookingCode = request.BookingCode.Trim().ToUpperInvariant();
            var booking = await _db.Bookings
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.BookingCode == normalizedBookingCode,
                    cancellationToken);
            if (booking is null ||
                !BookingGuestAccessPolicy.IsValid(booking, accessToken, DateTime.UtcNow))
            {
                return NotFound(new { Message = "The booking or secure access link is invalid." });
            }

            guestId = booking.GuestId;
        }

        var hasOpenRequest = await _db.PrivacyRequests.AnyAsync(
            item => item.GuestId == guestId &&
                    item.Type == request.Type &&
                    (item.Status == DataSubjectRequestStatus.Pending ||
                     item.Status == DataSubjectRequestStatus.InProgress),
            cancellationToken);
        if (hasOpenRequest)
            return Conflict(new { Message = "An open request of this type already exists." });

        var now = DateTime.UtcNow;
        var entity = new PrivacyRequest
        {
            Id = Guid.NewGuid(),
            GuestId = guestId,
            RequestedByUserId = requestedByUserId,
            Type = request.Type,
            Status = DataSubjectRequestStatus.Pending,
            Details = request.Details?.Trim(),
            RequestedAtUtc = now,
            DueAtUtc = now.AddDays(30)
        };
        _db.PrivacyRequests.Add(entity);
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = requestedByUserId ?? BookingPaymentPolicy.SystemActorId,
            Action = "PRIVACY_REQUEST_CREATED",
            EntityType = "PrivacyRequest",
            EntityId = entity.Id.ToString(),
            NewDataJson = JsonSerializer.Serialize(new
            {
                entity.GuestId,
                Type = entity.Type.ToString(),
                Status = entity.Status.ToString(),
                entity.RequestedAtUtc
            }),
            CreatedAt = now
        });
        await _db.SaveChangesAsync(cancellationToken);

        return AcceptedAtAction(nameof(GetMyRequests), new { }, ToDto(entity));
    }

    [HttpGet("requests/mine")]
    [Authorize(Roles = "Client")]
    public async Task<IActionResult> GetMyRequests(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var guestId = await _db.Users
            .AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(item => item.GuestId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(guestId)) return Ok(Array.Empty<PrivacyRequestDto>());

        return Ok(await _db.PrivacyRequests
            .AsNoTracking()
            .Where(item => item.GuestId == guestId)
            .OrderByDescending(item => item.RequestedAtUtc)
            .Select(item => ToDto(item))
            .ToListAsync(cancellationToken));
    }

    [HttpGet("requests")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetRequests(
        [FromQuery] DataSubjectRequestStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.PrivacyRequests.AsNoTracking();
        if (status.HasValue) query = query.Where(item => item.Status == status);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(item => item.RequestedAtUtc)
            .ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => ToDto(item))
            .ToListAsync(cancellationToken);
        return Ok(new { Items = items, TotalCount = total, Page = page, PageSize = pageSize });
    }

    [HttpPatch("requests/{id:guid}/status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateRequest(
        Guid id,
        [FromBody] UpdatePrivacyRequestRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var actorId)) return Unauthorized();

        if (request.Status == DataSubjectRequestStatus.Pending)
            return BadRequest(new { Message = "A processed request cannot be returned to pending." });
        if (request.Status is DataSubjectRequestStatus.Completed or DataSubjectRequestStatus.Rejected &&
            string.IsNullOrWhiteSpace(request.ResolutionNotes))
        {
            return BadRequest(new { Message = "Resolution notes are required when closing a request." });
        }
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var entity = await _db.PrivacyRequests
                .FromSqlInterpolated($"SELECT * FROM privacy_requests WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (entity is null) return NotFound();
            if (entity.Status is DataSubjectRequestStatus.Completed or DataSubjectRequestStatus.Rejected)
                return Conflict(new { Message = "This privacy request is already closed." });

            var oldStatus = entity.Status;
            var now = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.IdentityVerificationReference))
            {
                var evidence = request.IdentityVerificationReference.Trim();
                if (entity.IdentityVerificationReference is not null &&
                    !string.Equals(entity.IdentityVerificationReference, evidence, StringComparison.Ordinal))
                {
                    return Conflict(new { Message = "Identity verification evidence cannot be replaced." });
                }
                entity.IdentityVerifiedAtUtc ??= now;
                entity.IdentityVerifiedByUserId ??= actorId;
                entity.IdentityVerificationReference ??= evidence;
            }

            entity.Status = request.Status;
            entity.ResolutionNotes = request.ResolutionNotes?.Trim();
            if (request.Status == DataSubjectRequestStatus.Completed)
            {
                if (!entity.IdentityVerifiedAtUtc.HasValue)
                    return BadRequest(new { Message = "Identity verification evidence is required before fulfillment." });
                if (string.IsNullOrWhiteSpace(request.FulfillmentEvidenceReference))
                    return BadRequest(new { Message = "Secure delivery or action evidence is required before fulfillment." });
                var fulfillment = await _privacyData.FulfillAsync(
                    entity,
                    request,
                    actorId,
                    now,
                    cancellationToken);
                entity.FulfilledAtUtc = now;
                entity.FulfillmentEvidenceReference = request.FulfillmentEvidenceReference.Trim();
                entity.FulfillmentDigest = fulfillment.Digest;
                entity.ExportGeneratedAtUtc = fulfillment.ExportGeneratedAtUtc;
            }
            if (request.Status is DataSubjectRequestStatus.Completed or DataSubjectRequestStatus.Rejected)
            {
                entity.ResolvedAtUtc = now;
                entity.ResolvedByUserId = actorId;
            }

            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ProfileId = actorId,
                Action = request.Status == DataSubjectRequestStatus.Completed
                    ? "PRIVACY_REQUEST_FULFILLED"
                    : "PRIVACY_REQUEST_STATUS_CHANGED",
                EntityType = "PrivacyRequest",
                EntityId = entity.Id.ToString(),
                OldDataJson = JsonSerializer.Serialize(new { Status = oldStatus.ToString() }),
                NewDataJson = JsonSerializer.Serialize(new
                {
                    Status = entity.Status.ToString(),
                    entity.IdentityVerifiedAtUtc,
                    entity.FulfilledAtUtc,
                    entity.FulfillmentDigest,
                    entity.ResolvedAtUtc,
                    entity.ResolvedByUserId
                }),
                CreatedAt = now
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Ok(ToDto(entity));
        });
    }

    [HttpPost("guests/{id}/legal-hold")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PlaceLegalHold(
        string id,
        [FromBody] PlaceLegalHoldRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _privacyData.PlaceLegalHoldAsync(id, request, userId, cancellationToken);
        return Ok(result);
    }

    [HttpDelete("guests/{id}/legal-hold")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReleaseLegalHold(
        string id,
        [FromBody] ReleaseLegalHoldRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _privacyData.ReleaseLegalHoldAsync(id, request, userId, cancellationToken);
        return Ok(result);
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier),
        out userId);

    public static PrivacyRequestDto ToDto(PrivacyRequest request) => new(
        request.Id,
        request.GuestId,
        request.Type,
        request.Status,
        request.Details,
        request.RequestedAtUtc,
        request.DueAtUtc,
        request.IdentityVerifiedAtUtc,
        request.IdentityVerificationReference,
        request.FulfilledAtUtc,
        request.FulfillmentEvidenceReference,
        request.FulfillmentDigest,
        request.ExportGeneratedAtUtc,
        request.ResolvedAtUtc,
        request.ResolutionNotes);
}
