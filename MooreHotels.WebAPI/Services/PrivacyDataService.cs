using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.WebAPI.Services;

public sealed record PrivacyFulfillmentResult(string Digest, DateTime? ExportGeneratedAtUtc);

public sealed class PrivacyDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly MooreHotelsDbContext _db;
    private readonly PrivacySettings _settings;
    private readonly IMediaDeletionOutbox _mediaDeletionOutbox;

    public PrivacyDataService(
        MooreHotelsDbContext db,
        IOptions<PrivacySettings> settings,
        IMediaDeletionOutbox mediaDeletionOutbox)
    {
        _db = db;
        _settings = settings.Value;
        _mediaDeletionOutbox = mediaDeletionOutbox;
    }

    public async Task<IReadOnlyDictionary<string, object?>> BuildExportAsync(
        string guestId,
        DateTime exportedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var guest = await _db.Guests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == guestId, cancellationToken)
            ?? throw new NotFoundException("Guest profile not found.");
        var account = await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(item => item.GuestId == guest.Id, cancellationToken);
        var bookings = await _db.Bookings.AsNoTracking().AsSplitQuery()
            .Include(item => item.RoomType)
            .Include(item => item.ReservationRooms).ThenInclude(item => item.AssignedRoom)
            .Include(item => item.AddOns).ThenInclude(item => item.AddOnService)
            .Include(item => item.Quote)!.ThenInclude(item => item!.Lines)
            .Include(item => item.Amendments)
            .Include(item => item.Folio)!.ThenInclude(item => item!.Entries)
            .Where(item => item.GuestId == guest.Id)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        var bookingIds = bookings.Select(item => item.Id).ToArray();
        var bookingCodes = bookings.Select(item => item.BookingCode).ToArray();
        var privacyRequests = await _db.PrivacyRequests.AsNoTracking()
            .Where(item => item.GuestId == guest.Id)
            .OrderByDescending(item => item.RequestedAtUtc)
            .ToListAsync(cancellationToken);
        var privacyRequestIds = privacyRequests.Select(item => item.Id.ToString()).ToArray();
        var folioIds = bookings.Where(item => item.Folio is not null)
            .Select(item => item.Folio!.Id.ToString()).ToArray();
        var auditEntityIds = bookingIds.Select(item => item.ToString())
            .Concat(privacyRequestIds).Concat(folioIds).Append(guest.Id).ToArray();
        var notes = await _db.GuestNotes.AsNoTracking()
            .Where(item => item.GuestId == guest.Id)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new { item.Id, item.Body, item.IsSensitive, item.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        var visits = await _db.VisitRecords.AsNoTracking()
            .Where(item => item.GuestId == guest.Id)
            .OrderBy(item => item.Timestamp)
            .Select(item => new
            {
                item.Id,
                item.BookingCode,
                item.RoomNumber,
                item.Action,
                item.Timestamp
            })
            .ToListAsync(cancellationToken);
        var notifications = await _db.Notifications.AsNoTracking()
            .Where(item => item.BookingCode != null && bookingCodes.Contains(item.BookingCode))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.Title,
                item.Message,
                item.BookingCode,
                item.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var payments = await _db.MonnifyTransactions.AsNoTracking()
            .Where(item => item.BookingId.HasValue && bookingIds.Contains(item.BookingId.Value))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.BookingCode,
                item.TransactionReference,
                item.MonnifyReference,
                item.Amount,
                item.Fee,
                item.SettledAmount,
                item.Status,
                item.PaymentMethod,
                item.VerifiedAtUtc,
                item.CreatedAt,
                item.UpdatedAt
            })
            .ToListAsync(cancellationToken);
        var channelMappings = await _db.ChannelReservationMappings.AsNoTracking()
            .Include(item => item.Channel)
            .Where(item => bookingIds.Contains(item.BookingId))
            .OrderBy(item => item.LinkedAtUtc)
            .Select(item => new
            {
                item.Id,
                Channel = item.Channel != null ? item.Channel.Name : null,
                item.ExternalReservationId,
                item.BookingId,
                item.LinkedAtUtc
            })
            .ToListAsync(cancellationToken);
        var merges = await _db.GuestMerges.AsNoTracking()
            .Where(item => item.PrimaryGuestId == guest.Id || item.DuplicateGuestId == guest.Id)
            .OrderBy(item => item.MergedAtUtc)
            .Select(item => new
            {
                item.Id,
                item.PrimaryGuestId,
                item.DuplicateGuestId,
                item.EvidenceType,
                item.Reason,
                item.MovedBookingCount,
                item.MergedAtUtc
            })
            .ToListAsync(cancellationToken);
        var auditTrail = await _db.AuditLogs.AsNoTracking()
            .Where(item => auditEntityIds.Contains(item.EntityId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new { item.Id, item.Action, item.EntityType, item.EntityId, item.CreatedAt })
            .ToListAsync(cancellationToken);
        var verificationHistory = await _db.BookingEmailVerifications.AsNoTracking()
            .Where(item => item.Email == guest.Email)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new
            {
                item.Id,
                item.Email,
                item.CreatedAtUtc,
                item.ExpiresAtUtc,
                item.ConsumedAtUtc
            })
            .ToListAsync(cancellationToken);
        var queuedMessages = await _db.EmailOutboxMessages.AsNoTracking()
            .Where(item => item.DataSubjectGuestId == guest.Id ||
                           item.Recipient == guest.Email ||
                           account != null && item.Recipient == account.Email)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new
            {
                item.Id,
                item.Template,
                item.Recipient,
                item.AttemptCount,
                item.NextAttemptAtUtc,
                item.LastErrorCode,
                item.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new Dictionary<string, object?>
        {
            ["exportedAtUtc"] = exportedAtUtc,
            ["processingInformation"] = new
            {
                Controller = "Moore Hotels & Suites",
                Purposes = new[]
                {
                    "Reservation and stay administration", "Guest communications",
                    "Payment and refund reconciliation", "Safety and property operations",
                    "Legal, accounting, fraud-prevention and audit obligations"
                },
                Categories = new[]
                {
                    "Account and contact data", "Reservation and stay data", "Guest preferences and notes",
                    "Payment and folio data", "Communications and operational audit data"
                },
                RecipientCategories = new[]
                {
                    "Authorized hotel staff", "Hosting and database providers",
                    "Transactional email and media providers", "Enabled payment or distribution providers"
                },
                GuestRetentionDays = _settings.GuestRetentionDays,
                AccountInactivityRetentionDays = _settings.InactiveAccountRetentionDays,
                _settings.PrivacyPolicyUrl,
                _settings.CurrentPrivacyPolicyVersion
            },
            ["account"] = account is null ? null : new
            {
                account.Id,
                account.Name,
                account.Email,
                account.PhoneNumber,
                Role = account.Role.ToString(),
                Status = account.Status.ToString(),
                account.CreatedAt,
                account.PrivacyPolicyVersion,
                account.PrivacyPolicyAcceptedAtUtc
            },
            ["guest"] = new
            {
                guest.Id,
                guest.FirstName,
                guest.LastName,
                guest.Email,
                guest.Phone,
                guest.EmailVerifiedAtUtc,
                guest.PhoneVerifiedAtUtc,
                Preferences = DeserializeJson(guest.PreferencesJson),
                guest.AvatarUrl,
                guest.CreatedAt,
                guest.AnonymizedAtUtc,
                guest.ProcessingRestrictedAtUtc,
                guest.MarketingObjectedAtUtc
            },
            ["bookings"] = bookings.Select(booking => new
            {
                booking.Id,
                booking.BookingCode,
                booking.CheckIn,
                booking.CheckOut,
                booking.AdultCount,
                booking.ChildCount,
                booking.RoomQuantity,
                Status = booking.Status.ToString(),
                booking.Currency,
                booking.RoomSubtotal,
                booking.DiscountAmount,
                booking.IncludedTaxAmount,
                booking.TaxAmount,
                booking.FeeAmount,
                booking.Amount,
                PaymentStatus = booking.PaymentStatus.ToString(),
                PaymentMethod = booking.PaymentMethod?.ToString(),
                booking.TransactionReference,
                booking.PaymentProviderReference,
                booking.PaymentConfirmedAtUtc,
                booking.RefundReference,
                booking.RefundAmount,
                booking.RefundChannel,
                booking.RefundEvidenceType,
                booking.RefundNotes,
                booking.RefundApprovedAtUtc,
                booking.RefundProcessedAtUtc,
                booking.Notes,
                StatusHistory = DeserializeJson(booking.StatusHistoryJson ?? "[]"),
                booking.CancelledAtUtc,
                booking.PrivacyPolicyVersion,
                booking.BookingTermsVersion,
                booking.PoliciesAcceptedAtUtc,
                booking.ReservationPolicyVersion,
                booking.FreeCancellationHours,
                booking.CancellationPenaltyPercent,
                booking.DepositPercent,
                booking.NoShowPenaltyPercent,
                booking.CancellationPenaltyAmount,
                booking.NoShowPenaltyAmount,
                booking.CreatedAt,
                RoomType = booking.RoomType?.Name,
                Rooms = booking.ReservationRooms.OrderBy(item => item.Sequence).Select(item => new
                {
                    item.Id,
                    item.Sequence,
                    item.RoomTypeCode,
                    item.RoomTypeName,
                    item.AssignedRoomId,
                    RoomNumber = item.AssignedRoom?.RoomNumber,
                    item.AssignedAtUtc,
                    item.CreatedAtUtc
                }),
                AddOns = booking.AddOns.OrderBy(item => item.AddedAtUtc).Select(item => new
                {
                    item.Id,
                    Service = item.AddOnService?.Name,
                    item.Quantity,
                    item.UnitPrice,
                    item.TotalPrice,
                    item.Notes,
                    item.AddedAtUtc
                }),
                Quote = booking.Quote is null ? null : new
                {
                    booking.Quote.Id,
                    booking.Quote.Currency,
                    booking.Quote.RoomSubtotal,
                    booking.Quote.DiscountAmount,
                    booking.Quote.IncludedTaxAmount,
                    booking.Quote.TaxAmount,
                    booking.Quote.FeeAmount,
                    booking.Quote.TotalAmount,
                    booking.Quote.CreatedAtUtc,
                    booking.Quote.ExpiresAtUtc,
                    booking.Quote.ConsumedAtUtc,
                    Lines = booking.Quote.Lines.OrderBy(item => item.SortOrder).Select(item => new
                    {
                        item.Type,
                        item.Code,
                        item.Description,
                        item.StayDate,
                        item.Quantity,
                        item.UnitAmount,
                        item.Amount,
                        item.IsInclusive
                    })
                },
                Amendments = booking.Amendments.OrderBy(item => item.AmendedAtUtc).Select(item => new
                {
                    item.Id,
                    item.PreviousAmount,
                    item.NewAmount,
                    item.PriceDifference,
                    item.RoomTypeId,
                    item.RoomQuantity,
                    item.CheckIn,
                    item.CheckOut,
                    item.AdultCount,
                    item.ChildCount,
                    item.Reason,
                    item.AmendedAtUtc
                }),
                Folio = booking.Folio is null ? null : new
                {
                    booking.Folio.Id,
                    booking.Folio.Currency,
                    booking.Folio.Status,
                    booking.Folio.OpenedAtUtc,
                    booking.Folio.ClosedAtUtc,
                    Entries = booking.Folio.Entries.OrderBy(item => item.PostedAtUtc).Select(item => new
                    {
                        item.Id,
                        item.Type,
                        item.Direction,
                        item.Amount,
                        item.Currency,
                        item.Description,
                        item.SourceType,
                        item.SourceId,
                        item.ExternalReference,
                        item.ReversesEntryId,
                        item.PostedAtUtc,
                        item.Notes
                    })
                }
            }),
            ["guestNotes"] = notes,
            ["stayActivity"] = visits,
            ["notifications"] = notifications,
            ["paymentProviderTransactions"] = payments,
            ["distributionMappings"] = channelMappings,
            ["profileMerges"] = merges,
            ["verificationHistory"] = verificationHistory,
            ["pendingCommunications"] = queuedMessages,
            ["auditTrail"] = auditTrail,
            ["privacyRequests"] = privacyRequests.Select(item => new
            {
                item.Id,
                item.Type,
                item.Status,
                item.Details,
                item.RequestedAtUtc,
                item.DueAtUtc,
                item.IdentityVerifiedAtUtc,
                item.IdentityVerificationReference,
                item.FulfilledAtUtc,
                item.FulfillmentEvidenceReference,
                item.FulfillmentDigest,
                item.ExportGeneratedAtUtc,
                item.ResolvedAtUtc,
                item.ResolutionNotes
            })
        };
    }

    public async Task<PrivacyFulfillmentResult> FulfillAsync(
        PrivacyRequest entity,
        UpdatePrivacyRequestRequest request,
        Guid actorId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        object actionEvidence;
        DateTime? exportGeneratedAtUtc = null;
        if (entity.Type is DataSubjectRequestType.Rectification or
            DataSubjectRequestType.Erasure or
            DataSubjectRequestType.Restriction or
            DataSubjectRequestType.Objection)
        {
            await LockGuestAsync(entity.GuestId, cancellationToken);
        }
        switch (entity.Type)
        {
            case DataSubjectRequestType.Access:
            case DataSubjectRequestType.Portability:
                actionEvidence = await BuildExportAsync(entity.GuestId, now, cancellationToken);
                exportGeneratedAtUtc = now;
                break;
            case DataSubjectRequestType.Rectification:
                actionEvidence = await ApplyRectificationAsync(
                    entity.GuestId,
                    request.Rectification ?? throw new BadRequestException(
                        "Structured corrections are required to complete a rectification request."),
                    cancellationToken);
                break;
            case DataSubjectRequestType.Erasure:
                if (!request.ConfirmAction)
                    throw new BadRequestException("Explicit erasure confirmation is required.");
                actionEvidence = await ApplyErasureAsync(entity.GuestId, cancellationToken);
                break;
            case DataSubjectRequestType.Restriction:
                if (!request.ConfirmAction)
                    throw new BadRequestException("Explicit processing-restriction confirmation is required.");
                var restrictedGuest = await RequireGuestAsync(entity.GuestId, cancellationToken);
                restrictedGuest.ProcessingRestrictedAtUtc = now;
                actionEvidence = new { restrictedGuest.Id, restrictedGuest.ProcessingRestrictedAtUtc };
                break;
            case DataSubjectRequestType.Objection:
                if (!request.ConfirmAction)
                    throw new BadRequestException("Explicit objection confirmation is required.");
                var objectingGuest = await RequireGuestAsync(entity.GuestId, cancellationToken);
                objectingGuest.MarketingObjectedAtUtc = now;
                var preferences = ParsePreferences(objectingGuest.PreferencesJson) with { MarketingOptIn = false };
                objectingGuest.PreferencesJson = JsonSerializer.Serialize(preferences, JsonOptions);
                actionEvidence = new { objectingGuest.Id, objectingGuest.MarketingObjectedAtUtc };
                break;
            default:
                throw new BadRequestException("The privacy request type is not supported.");
        }

        return new PrivacyFulfillmentResult(Hash(actionEvidence), exportGeneratedAtUtc);
    }

    private async Task<object> ApplyRectificationAsync(
        string guestId,
        GuestRectificationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.FirstName is null && request.LastName is null && request.Email is null && request.Phone is null)
            throw new BadRequestException("At least one structured correction is required.");
        var guest = await RequireGuestAsync(guestId, cancellationToken);
        var account = await _db.Users.SingleOrDefaultAsync(item => item.GuestId == guest.Id, cancellationToken);
        if (request.FirstName is not null) guest.FirstName = RequireText(request.FirstName, "First name");
        if (request.LastName is not null) guest.LastName = RequireText(request.LastName, "Last name");
        if (request.Email is not null)
        {
            var email = request.Email.Trim().ToLowerInvariant();
            var normalizedAccountEmail = email.ToUpperInvariant();
            var accountId = account?.Id;
            if (await _db.Users.AnyAsync(
                    item => (!accountId.HasValue || item.Id != accountId.Value) &&
                            item.NormalizedEmail == normalizedAccountEmail,
                    cancellationToken))
            {
                throw new ConflictException("The corrected email is already assigned to another account.");
            }
            guest.Email = email;
            guest.NormalizedEmail = email;
            guest.EmailVerifiedAtUtc = null;
            if (account is not null)
            {
                account.Email = email;
                account.UserName = email;
                account.NormalizedEmail = normalizedAccountEmail;
                account.NormalizedUserName = normalizedAccountEmail;
                account.EmailConfirmed = false;
                account.SecurityStamp = Guid.NewGuid().ToString("N");
            }
        }
        if (request.Phone is not null)
        {
            var phone = request.Phone.Trim();
            guest.Phone = phone;
            guest.NormalizedPhone = NormalizePhone(phone);
            guest.PhoneVerifiedAtUtc = null;
            if (account is not null)
            {
                account.PhoneNumber = phone;
                account.PhoneNumberConfirmed = false;
                account.SecurityStamp = Guid.NewGuid().ToString("N");
            }
        }
        if (account is not null && (request.FirstName is not null || request.LastName is not null))
        {
            account.Name = $"{guest.FirstName} {guest.LastName}".Trim();
        }
        return new
        {
            guest.Id,
            CorrectedFields = new[]
            {
                request.FirstName is null ? null : nameof(request.FirstName),
                request.LastName is null ? null : nameof(request.LastName),
                request.Email is null ? null : nameof(request.Email),
                request.Phone is null ? null : nameof(request.Phone)
            }.Where(item => item is not null).ToArray()
        };
    }

    private async Task<object> ApplyErasureAsync(string guestId, CancellationToken cancellationToken)
    {
        var guest = await RequireGuestAsync(guestId, cancellationToken);
        if (guest.IsUnderLegalHold)
        {
            throw new BadRequestException("Guest data is subject to an active legal hold and cannot be erased.");
        }

        var activeBooking = await _db.Bookings.AsNoTracking().AnyAsync(item =>
            item.GuestId == guest.Id && item.Status != BookingStatus.Cancelled &&
            item.Status != BookingStatus.CheckedOut && item.Status != BookingStatus.NoShow,
            cancellationToken);
        var folios = await _db.Folios.AsNoTracking().Include(item => item.Entries)
            .Where(item => item.Booking != null && item.Booking.GuestId == guest.Id)
            .ToListAsync(cancellationToken);
        if (activeBooking || folios.Any(item => FolioAccounting.Calculate(item.Entries).Balance != 0))
        {
            throw new ConflictException(
                "Erasure cannot complete while an active reservation or unsettled folio requires the guest identity.");
        }

        var bookingCodes = await _db.Bookings.Where(item => item.GuestId == guest.Id)
            .Select(item => item.BookingCode).ToArrayAsync(cancellationToken);
        var oldEmail = guest.Email;
        var account = await _db.Users.SingleOrDefaultAsync(item => item.GuestId == guest.Id, cancellationToken);
        var now = DateTime.UtcNow;
        guest.FirstName = "Former";
        guest.LastName = "Guest";
        guest.Email = $"anonymized-{guest.Id.ToLowerInvariant()}@privacy.invalid";
        guest.NormalizedEmail = guest.Email;
        guest.Phone = "REDACTED";
        guest.NormalizedPhone = string.Empty;
        guest.EmailVerifiedAtUtc = null;
        guest.PhoneVerifiedAtUtc = null;
        guest.PreferencesJson = "{}";
        guest.AvatarUrl = null;
        guest.AnonymizedAtUtc = now;
        guest.ProcessingRestrictedAtUtc = now;
        guest.MarketingObjectedAtUtc = now;
        if (account is not null)
        {
            if (!string.IsNullOrWhiteSpace(account.AvatarPublicId))
            {
                _db.MediaDeletionJobs.Add(_mediaDeletionOutbox.Create(
                    account.AvatarPublicId,
                    "PrivacyErasure",
                    guest.Id));
            }
            var erasedEmail = $"erased-{account.Id:N}@privacy.invalid";
            account.Name = "Former Guest";
            account.Email = erasedEmail;
            account.UserName = erasedEmail;
            account.NormalizedEmail = erasedEmail.ToUpperInvariant();
            account.NormalizedUserName = account.NormalizedEmail;
            account.PhoneNumber = null;
            account.EmailConfirmed = false;
            account.PhoneNumberConfirmed = false;
            account.AvatarUrl = null;
            account.AvatarPublicId = null;
            account.Status = ProfileStatus.Suspended;
            account.StatusChangedAtUtc = now;
            account.LastAuthenticatedAtUtc = null;
            account.AnonymizedAtUtc = now;
            account.PasswordHash = null;
            account.TwoFactorEnabled = false;
            account.LockoutEnd = DateTimeOffset.MaxValue;
            account.SecurityStamp = Guid.NewGuid().ToString("N");

            await _db.UserTokens
                .Where(token => token.UserId == account.Id)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.UserLogins
                .Where(login => login.UserId == account.Id)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.UserClaims
                .Where(claim => claim.UserId == account.Id)
                .ExecuteDeleteAsync(cancellationToken);
        }
        await _db.Bookings.Where(item => item.GuestId == guest.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Notes, (string?)null)
                .SetProperty(item => item.RefundNotes, (string?)null)
                .SetProperty(item => item.GuestAccessTokenHash, (string?)null), cancellationToken);
        await _db.BookingAddOns.Where(item => item.Booking != null && item.Booking.GuestId == guest.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Notes, (string?)null), cancellationToken);
        await _db.GuestNotes.Where(item => item.GuestId == guest.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Body, "Removed by privacy request")
                .SetProperty(item => item.IsSensitive, false), cancellationToken);
        await _db.VisitRecords.Where(item => item.GuestId == guest.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GuestName, "Former Guest"), cancellationToken);
        await _db.Notifications.Where(item => item.BookingCode != null && bookingCodes.Contains(item.BookingCode))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Title, "Archived booking activity")
                .SetProperty(item => item.Message, "Archived booking activity."), cancellationToken);
        await _db.BookingEmailVerifications.Where(item => item.Email == oldEmail)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.EmailOutboxMessages.Where(item =>
                item.DataSubjectGuestId == guest.Id || item.Recipient == oldEmail)
            .ExecuteDeleteAsync(cancellationToken);
        return new { guest.Id, guest.AnonymizedAtUtc, AccountSuspended = account is not null };
    }

    public async Task<LegalHoldDto> PlaceLegalHoldAsync(
        string guestId,
        PlaceLegalHoldRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated actor is invalid.");
        var reason = RequireEvidence(request.Reason, "Legal-hold reason", 500);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var guest = await GetLockedGuestAsync(guestId, cancellationToken);
            if (guest.AnonymizedAtUtc.HasValue)
                throw new ConflictException("Cannot place a legal hold on an already anonymized guest record.");
            if (guest.IsUnderLegalHold)
                throw new ConflictException("The guest record is already under a legal hold.");
            var now = DateTime.UtcNow;
            guest.IsUnderLegalHold = true;
            guest.LegalHoldPlacedAtUtc = now;
            guest.LegalHoldReason = reason;
            guest.LegalHoldPlacedByUserId = actorId;
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ProfileId = actorId,
                Action = "GUEST_LEGAL_HOLD_PLACED",
                EntityType = "Guest",
                EntityId = guest.Id,
                NewDataJson = JsonSerializer.Serialize(new { guest.LegalHoldReason, PlacedAtUtc = now }),
                CreatedAt = now
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LegalHoldDto(guest.Id, true, now, guest.LegalHoldReason, actorId);
        });
    }

    public async Task<LegalHoldDto> ReleaseLegalHoldAsync(
        string guestId,
        ReleaseLegalHoldRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated actor is invalid.");
        var reason = RequireEvidence(request.Reason, "Legal-hold release reason", 500);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var guest = await GetLockedGuestAsync(guestId, cancellationToken);
            if (!guest.IsUnderLegalHold)
                throw new ConflictException("The guest record is not currently under a legal hold.");
            var now = DateTime.UtcNow;
            guest.IsUnderLegalHold = false;
            guest.LegalHoldPlacedAtUtc = null;
            guest.LegalHoldReason = null;
            guest.LegalHoldPlacedByUserId = null;
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ProfileId = actorId,
                Action = "GUEST_LEGAL_HOLD_RELEASED",
                EntityType = "Guest",
                EntityId = guest.Id,
                NewDataJson = JsonSerializer.Serialize(new { ReleaseReason = reason, ReleasedAtUtc = now }),
                CreatedAt = now
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LegalHoldDto(guest.Id, false, null, null, null);
        });
    }

    private async Task LockGuestAsync(string guestId, CancellationToken cancellationToken)
    {
        var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM guests WHERE \"Id\" = {guestId} FOR UPDATE",
            cancellationToken);
        _ = affected;
    }

    private async Task<Guest> GetLockedGuestAsync(
        string guestId,
        CancellationToken cancellationToken) =>
        await _db.Guests
            .FromSqlInterpolated(
                $"SELECT * FROM guests WHERE \"Id\" = {guestId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException("Guest profile not found.");

    private async Task<Guest> RequireGuestAsync(string guestId, CancellationToken cancellationToken) =>
        await _db.Guests.SingleOrDefaultAsync(item => item.Id == guestId, cancellationToken)
        ?? throw new NotFoundException("Guest profile not found.");

    private static GuestPreferencesDto ParsePreferences(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<GuestPreferencesDto>(value, JsonOptions) ??
                   new GuestPreferencesDto(null, null, null, null, false);
        }
        catch (JsonException)
        {
            return new GuestPreferencesDto(null, null, null, null, false);
        }
    }

    private static object? DeserializeJson(string value)
    {
        try { return JsonSerializer.Deserialize<object>(value, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static string Hash(object value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));

    private static string RequireText(string value, string field)
    {
        var cleaned = value.Trim();
        if (cleaned.Length == 0) throw new BadRequestException($"{field} is required.");
        return cleaned;
    }

    private static string RequireEvidence(
        string? value,
        string field,
        int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length < 4 || cleaned.Length > maximumLength ||
            cleaned.Any(char.IsControl))
        {
            throw new BadRequestException(
                $"{field} must be between 4 and {maximumLength} characters.");
        }

        return cleaned;
    }

    private static string NormalizePhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return value.TrimStart().StartsWith('+') && digits.Length > 0 ? $"+{digits}" : digits;
    }
}
