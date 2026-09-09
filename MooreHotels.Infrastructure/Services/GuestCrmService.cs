using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Services;

public sealed class GuestCrmService : IGuestCrmService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex EvidenceReferencePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:/-]{9,159}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private readonly MooreHotelsDbContext _db;

    public GuestCrmService(MooreHotelsDbContext db) => _db = db;

    public async Task<GuestCrmProfileDto> GetProfileAsync(
        string guestId,
        bool includeSensitiveNotes,
        CancellationToken cancellationToken = default)
    {
        var guest = await ResolveGuestAsync(guestId, cancellationToken);
        var sourceIds = await _db.Guests.AsNoTracking()
            .Where(item => item.Id == guest.Id || item.MergedIntoGuestId == guest.Id)
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        var stays = await _db.Bookings.AsNoTracking().Include(item => item.RoomType)
            .Where(item => item.GuestId == guest.Id)
            .OrderByDescending(item => item.CheckIn).Take(100)
            .Select(item => new GuestStayHistoryDto(
                item.Id, item.BookingCode, item.CheckIn, item.CheckOut,
                item.RoomType != null ? item.RoomType.Name : string.Empty,
                item.RoomQuantity, item.Status.ToString(), item.Amount))
            .ToListAsync(cancellationToken);
        var notes = await _db.GuestNotes.AsNoTracking()
            .Where(item => sourceIds.Contains(item.GuestId) &&
                           (includeSensitiveNotes || !item.IsSensitive))
            .OrderByDescending(item => item.CreatedAtUtc).Take(200)
            .Select(item => new GuestNoteDto(
                item.Id, item.Body, item.IsSensitive, item.CreatedAtUtc, item.CreatedByUserId))
            .ToListAsync(cancellationToken);
        return new GuestCrmProfileDto(
            guest.Id, guest.FirstName, guest.LastName, guest.Email, guest.Phone,
            guest.EmailVerifiedAtUtc, guest.PhoneVerifiedAtUtc,
            ParsePreferences(guest.PreferencesJson), stays, notes);
    }

    public async Task<GuestPreferencesDto> UpdatePreferencesAsync(
        string guestId,
        UpdateGuestPreferencesRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireActorAsync(actorId, managementOnly: false, cancellationToken);
        var preferredLanguage = Clean(request.PreferredLanguage, "Preferred language", 40);
        var beddingPreference = Clean(request.BeddingPreference, "Bedding preference", 80);
        var dietaryNotes = Clean(request.DietaryNotes, "Dietary notes", 300);
        var accessibilityNeeds = Clean(request.AccessibilityNeeds, "Accessibility needs", 300);
        var consentReference = Clean(request.MarketingConsentReference,
            "Marketing consent reference", 160);
        if (request.MarketingOptIn &&
            (consentReference is null || !EvidenceReferencePattern.IsMatch(consentReference)))
        {
            throw new BadRequestException(
                "A non-sensitive consent or case reference is required for marketing opt-in.");
        }
        var guest = await ResolveGuestAsync(guestId, cancellationToken, tracked: true);
        var preferences = new GuestPreferencesDto(
            preferredLanguage, beddingPreference, dietaryNotes, accessibilityNeeds,
            request.MarketingOptIn);
        guest.PreferencesJson = JsonSerializer.Serialize(preferences, JsonOptions);
        AddAudit(actorId, "GUEST_PREFERENCES_UPDATED", guest.Id, null,
            JsonSerializer.Serialize(new
            {
                Fields = new[]
                {
                    nameof(request.PreferredLanguage), nameof(request.BeddingPreference),
                    nameof(request.DietaryNotes), nameof(request.AccessibilityNeeds),
                    nameof(request.MarketingOptIn)
                },
                request.MarketingOptIn,
                MarketingConsentReference = consentReference
            }));
        await _db.SaveChangesAsync(cancellationToken);
        return preferences;
    }

    public async Task<GuestNoteDto> AddNoteAsync(
        string guestId,
        AddGuestNoteRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireActorAsync(actorId, managementOnly: true, cancellationToken);
        var guest = await ResolveGuestAsync(guestId, cancellationToken);
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length is < 4 or > 1000 || body.Any(IsUnsafeControlCharacter))
            throw new BadRequestException("Guest notes must contain 4 to 1000 characters.");
        var note = new GuestNote
        {
            Id = Guid.NewGuid(),
            GuestId = guest.Id,
            Body = body,
            IsSensitive = request.IsSensitive,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = actorId
        };
        _db.GuestNotes.Add(note);
        AddAudit(actorId, "GUEST_NOTE_ADDED", guest.Id, null,
            JsonSerializer.Serialize(new { NoteId = note.Id, note.IsSensitive }));
        await _db.SaveChangesAsync(cancellationToken);
        return new GuestNoteDto(note.Id, note.Body, note.IsSensitive, note.CreatedAtUtc, actorId);
    }

    public async Task VerifyContactAsync(
        string guestId,
        VerifyGuestContactRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireActorAsync(actorId, managementOnly: true, cancellationToken);
        var contactType = request.ContactType?.Trim() ?? string.Empty;
        var evidenceReference = request.EvidenceReference?.Trim() ?? string.Empty;
        if (contactType is not ("Email" or "Phone") ||
            !EvidenceReferencePattern.IsMatch(evidenceReference))
            throw new BadRequestException("Contact verification evidence is invalid.");
        var guest = await ResolveGuestAsync(guestId, cancellationToken, tracked: true);
        var now = DateTime.UtcNow;
        if (contactType == "Email")
            guest.EmailVerifiedAtUtc = now;
        else if (contactType == "Phone")
            guest.PhoneVerifiedAtUtc = now;
        else throw new BadRequestException("Contact type must be Email or Phone.");
        AddAudit(actorId, $"GUEST_{contactType.ToUpperInvariant()}_VERIFIED", guest.Id, null,
            JsonSerializer.Serialize(new { EvidenceReference = evidenceReference, VerifiedAtUtc = now }));
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GuestDuplicateCandidateDto>> FindDuplicatesAsync(
        string guestId,
        CancellationToken cancellationToken = default)
    {
        var guest = await ResolveGuestAsync(guestId, cancellationToken);
        var email = NormalizeEmail(guest.Email);
        var phone = NormalizePhone(guest.Phone);
        var candidates = await _db.Guests.AsNoTracking()
            .Where(item => item.Id != guest.Id && item.MergedIntoGuestId == null &&
                ((!string.IsNullOrEmpty(email) && item.NormalizedEmail == email) ||
                 (!string.IsNullOrEmpty(phone) && item.NormalizedPhone == phone) ||
                 (EF.Functions.ILike(item.FirstName, guest.FirstName) &&
                  EF.Functions.ILike(item.LastName, guest.LastName))))
            .OrderBy(item => item.CreatedAt).Take(100)
            .Select(item => new { Guest = item, BookingCount = item.Bookings.Count })
            .ToListAsync(cancellationToken);
        return candidates.Select(item => new GuestDuplicateCandidateDto(
            item.Guest.Id, $"{item.Guest.FirstName} {item.Guest.LastName}".Trim(),
            item.Guest.Email, item.Guest.Phone, item.BookingCount,
            MatchReasons(guest, item.Guest))).ToArray();
    }

    public async Task<GuestMergeDto> MergeAsync(
        MergeGuestRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireActorAsync(actorId, managementOnly: true, cancellationToken);
        var primaryGuestId = NormalizeGuestId(request.PrimaryGuestId);
        var duplicateGuestId = NormalizeGuestId(request.DuplicateGuestId);
        if (primaryGuestId == duplicateGuestId)
            throw new BadRequestException("Primary and duplicate guest must be different records.");
        var evidenceType = request.EvidenceType?.Trim() ?? string.Empty;
        if (evidenceType is not ("VerifiedEmail" or "VerifiedPhone" or "GovernmentIdReviewed" or "ManualReview"))
            throw new BadRequestException("The merge evidence type is invalid.");
        var evidenceReference = request.Reason?.Trim() ?? string.Empty;
        if (!EvidenceReferencePattern.IsMatch(evidenceReference))
            throw new BadRequestException(
                "Reason must be a non-sensitive evidence or case reference without spaces.");
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var ids = new[] { primaryGuestId, duplicateGuestId }
                .OrderBy(item => item, StringComparer.Ordinal).ToArray();
            var locked = await _db.Guests.FromSqlInterpolated(
                    $"SELECT * FROM guests WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE")
                .ToListAsync(cancellationToken);
            var primary = locked.SingleOrDefault(item => item.Id == primaryGuestId)
                ?? throw new NotFoundException("Primary guest not found.");
            var duplicate = locked.SingleOrDefault(item => item.Id == duplicateGuestId)
                ?? throw new NotFoundException("Duplicate guest not found.");
            if (primary.MergedIntoGuestId is not null || duplicate.MergedIntoGuestId is not null)
                throw new ConflictException("One of the guest records has already been merged.");
            ValidateMergeEvidence(primary, duplicate, evidenceType);

            var primaryUser = await _db.Users.SingleOrDefaultAsync(item => item.GuestId == primary.Id, cancellationToken);
            var duplicateUser = await _db.Users.SingleOrDefaultAsync(item => item.GuestId == duplicate.Id, cancellationToken);
            if (primaryUser is not null && duplicateUser is not null)
                throw new ConflictException("Both guest records have client accounts; reconcile account ownership before merging.");
            if (duplicateUser is not null)
            {
                duplicateUser.GuestId = primary.Id;
                duplicateUser.SecurityStamp = Guid.NewGuid().ToString();
            }

            var moved = await _db.Bookings.Where(item => item.GuestId == duplicate.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GuestId, primary.Id), cancellationToken);
            await _db.PrivacyRequests.Where(item => item.GuestId == duplicate.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GuestId, primary.Id), cancellationToken);
            await _db.EmailOutboxMessages.Where(item => item.DataSubjectGuestId == duplicate.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.DataSubjectGuestId, primary.Id),
                    cancellationToken);
            if (NormalizeEmail(primary.Email) == NormalizeEmail(duplicate.Email))
                primary.EmailVerifiedAtUtc ??= duplicate.EmailVerifiedAtUtc;
            if (!string.IsNullOrEmpty(NormalizePhone(primary.Phone)) &&
                NormalizePhone(primary.Phone) == NormalizePhone(duplicate.Phone))
            {
                primary.PhoneVerifiedAtUtc ??= duplicate.PhoneVerifiedAtUtc;
            }
            if (primary.PreferencesJson == "{}" && duplicate.PreferencesJson != "{}")
                primary.PreferencesJson = duplicate.PreferencesJson;
            primary.NormalizedEmail = NormalizeEmail(primary.Email);
            primary.NormalizedPhone = NormalizePhone(primary.Phone);
            var now = DateTime.UtcNow;
            duplicate.MergedIntoGuestId = primary.Id;
            duplicate.MergedAtUtc = now;
            duplicate.MergedByUserId = actorId;
            var merge = new GuestMerge
            {
                Id = Guid.NewGuid(),
                PrimaryGuestId = primary.Id,
                DuplicateGuestId = duplicate.Id,
                EvidenceType = evidenceType,
                Reason = evidenceReference,
                MovedBookingCount = moved,
                MergedAtUtc = now,
                MergedByUserId = actorId
            };
            _db.GuestMerges.Add(merge);
            AddAudit(actorId, "GUEST_PROFILES_MERGED", primary.Id, null,
                JsonSerializer.Serialize(new { merge.DuplicateGuestId, merge.EvidenceType, merge.Reason, moved }));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new GuestMergeDto(
                merge.Id, primary.Id, duplicate.Id, moved, merge.EvidenceType,
                merge.Reason, merge.MergedAtUtc, actorId);
        });
    }

    private async Task<Guest> ResolveGuestAsync(string guestId, CancellationToken ct, bool tracked = false)
    {
        var normalizedId = NormalizeGuestId(guestId);
        var query = tracked ? _db.Guests.AsQueryable() : _db.Guests.AsNoTracking();
        var guest = await query.SingleOrDefaultAsync(item => item.Id == normalizedId, ct)
            ?? throw new NotFoundException("Guest not found.");
        if (guest.MergedIntoGuestId is null) return guest;
        return await query.SingleOrDefaultAsync(item => item.Id == guest.MergedIntoGuestId, ct)
            ?? throw new InvalidOperationException("The merged guest points to a missing primary profile.");
    }

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

    private static List<string> MatchReasons(Guest left, Guest right)
    {
        var reasons = new List<string>();
        if (NormalizeEmail(left.Email) == NormalizeEmail(right.Email)) reasons.Add("SameEmail");
        if (!string.IsNullOrEmpty(NormalizePhone(left.Phone)) && NormalizePhone(left.Phone) == NormalizePhone(right.Phone))
            reasons.Add("SamePhone");
        if (left.FirstName.Equals(right.FirstName, StringComparison.OrdinalIgnoreCase) &&
            left.LastName.Equals(right.LastName, StringComparison.OrdinalIgnoreCase)) reasons.Add("SameName");
        return reasons;
    }

    private static void ValidateMergeEvidence(Guest primary, Guest duplicate, string evidence)
    {
        var valid = evidence switch
        {
            "VerifiedEmail" => primary.EmailVerifiedAtUtc.HasValue && duplicate.EmailVerifiedAtUtc.HasValue &&
                               NormalizeEmail(primary.Email) == NormalizeEmail(duplicate.Email),
            "VerifiedPhone" => primary.PhoneVerifiedAtUtc.HasValue && duplicate.PhoneVerifiedAtUtc.HasValue &&
                               !string.IsNullOrEmpty(NormalizePhone(primary.Phone)) &&
                               NormalizePhone(primary.Phone) == NormalizePhone(duplicate.Phone),
            "GovernmentIdReviewed" or "ManualReview" => true,
            _ => false
        };
        if (!valid) throw new BadRequestException("The selected merge evidence is not supported by the guest records.");
    }

    private void AddAudit(Guid actorId, string action, string guestId, string? oldData, string? newData) =>
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actorId,
            Action = action,
            EntityType = "Guest",
            EntityId = guestId,
            OldDataJson = oldData,
            NewDataJson = newData,
            CreatedAt = DateTime.UtcNow
        });

    private async Task RequireActorAsync(
        Guid actorId,
        bool managementOnly,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated actor is invalid.");
        var actor = await _db.Users.AsNoTracking().SingleOrDefaultAsync(user =>
            user.Id == actorId && user.Status == ProfileStatus.Active,
            cancellationToken);
        if (actor is null)
            throw new UnauthorizedAccessException("The actor is not authorized for this guest CRM operation.");
        var allowed = actor.Role is UserRole.Admin or UserRole.Manager ||
                      !managementOnly && actor.Role == UserRole.Staff &&
                      actor.Department is "Reception" or "FrontDesk";
        if (!allowed)
            throw new UnauthorizedAccessException("The actor is not authorized for this guest CRM operation.");
    }

    private static string NormalizeGuestId(string? value)
    {
        var id = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (id.Length is < 4 or > 20 || id.Any(char.IsControl))
            throw new BadRequestException("Guest identifier is invalid.");
        return id;
    }

    private static string NormalizeEmail(string value) => value.Trim().ToLowerInvariant();
    private static string NormalizePhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return value.TrimStart().StartsWith('+') && digits.Length > 0 ? $"+{digits}" : digits;
    }
    private static string? Clean(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim();
        if (cleaned.Length > maximumLength || cleaned.Any(IsUnsafeControlCharacter))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }

    private static bool IsUnsafeControlCharacter(char value) =>
        char.IsControl(value) && value is not ('\r' or '\n' or '\t');
}
