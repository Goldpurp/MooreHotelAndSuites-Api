using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Exceptions;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Domain.Common;
using System.Text;

namespace MooreHotels.Application.Services;

public class ProfileService : IProfileService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IAuditService _auditService;
    private readonly IBookingRepository _bookingRepo;
    private readonly IGuestRepository _guestRepo;
    private readonly IEmailOutbox _emailOutbox;
    private readonly IApplicationTransaction _transaction;
    private readonly IStaffSessionRevocationService _sessionRevocation;
    private readonly IConfiguration _configuration;

    public ProfileService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAuditService auditService,
        IBookingRepository bookingRepo,
        IGuestRepository guestRepo,
        IEmailOutbox emailOutbox,
        IApplicationTransaction transaction,
        IStaffSessionRevocationService sessionRevocation,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _auditService = auditService;
        _bookingRepo = bookingRepo;
        _guestRepo = guestRepo;
        _emailOutbox = emailOutbox;
        _transaction = transaction;
        _sessionRevocation = sessionRevocation;
        _configuration = configuration;
    }

    public async Task<UserProfileDto> GetProfileAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new NotFoundException("User not found.");

        var guest = string.IsNullOrWhiteSpace(user.GuestId)
            ? null
            : await _guestRepo.GetByIdAsync(user.GuestId);

        return new UserProfileDto(
            user.Id,
            user.Name,
            user.Email!,
            user.PhoneNumber,
            user.Role.ToString(),
            user.Status.ToString(),
            user.AvatarUrl,
            user.EmailConfirmed,
            user.CreatedAt,
            guest?.Id,
            user.Department
        );
    }

    public async Task UpdateProfileAsync(Guid userId, UpdateProfileRequest request)
    {
        if (userId == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated user is invalid.");
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new NotFoundException("User account not found.");

        bool isChanged = false;
        bool emailChanged = false;
        var updatedFields = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            user.Name = RequireText(request.FullName, "Full name", 160);
            isChanged = true;
            updatedFields.Add("name");
        }

        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            if (normalizedEmail.Length > 254 ||
                !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(normalizedEmail))
            {
                throw new BadRequestException("Email address is invalid.");
            }
            if (!string.Equals(normalizedEmail, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
                    !await HasValidPasswordAsync(user, request.CurrentPassword))
                {
                    throw new UnauthorizedAccessException(
                        "Current password verification is required to change the sign-in email.");
                }
                var existing = await _userManager.FindByEmailAsync(normalizedEmail);
                if (existing != null && existing.Id != userId)
                    throw new BadRequestException("Email address is already associated with another account.");

                user.Email = normalizedEmail;
                user.UserName = normalizedEmail;
                user.EmailConfirmed = _configuration.GetValue<bool>("Runtime:AutoConfirmEmail");
                isChanged = true;
                emailChanged = true;
                updatedFields.Add("email");
            }
        }

        if (request.Phone != null)
        {
            var phone = request.Phone.Trim();
            if (phone.Length > 30 ||
                phone.Any(char.IsControl) ||
                (phone.Length > 0 &&
                 !new System.ComponentModel.DataAnnotations.PhoneAttribute().IsValid(phone)))
            {
                throw new BadRequestException("Phone number is invalid.");
            }
            user.PhoneNumber = phone;
            isChanged = true;
            updatedFields.Add("phone");
        }

        if (request.AvatarUrl != null)
        {
            throw new BadRequestException(
                "Avatar images must be changed through the dedicated profile avatar endpoint.");
        }

        if (isChanged)
        {
            await _transaction.ExecuteWithUserLockAsync(userId, async () =>
            {
                var result = await _userManager.UpdateAsync(user);
                if (!result.Succeeded)
                {
                    throw new BadRequestException(
                        string.Join(", ", result.Errors.Select(e => e.Description)));
                }

                if (emailChanged)
                {
                    var stamp = await _userManager.UpdateSecurityStampAsync(user);
                    if (!stamp.Succeeded)
                    {
                        throw new InvalidOperationException("Existing sessions could not be invalidated.");
                    }
                }

                var guest = string.IsNullOrWhiteSpace(user.GuestId)
                    ? null
                    : await _guestRepo.GetByIdAsync(user.GuestId);
                if (guest != null)
                {
                    if (!string.IsNullOrWhiteSpace(request.FullName))
                    {
                        var names = request.FullName.Trim().Split(' ', 2);
                        guest.FirstName = names[0];
                        guest.LastName = names.Length > 1 ? names[1] : "";
                    }

                    if (emailChanged)
                    {
                        guest.Email = user.Email!;
                        guest.NormalizedEmail = user.Email!.Trim().ToLowerInvariant();
                        guest.EmailVerifiedAtUtc = user.EmailConfirmed ? DateTime.UtcNow : null;
                    }
                    if (request.Phone != null)
                    {
                        guest.Phone = user.PhoneNumber ?? string.Empty;
                        var digits = new string(guest.Phone.Where(char.IsDigit).ToArray());
                        guest.NormalizedPhone = guest.Phone.TrimStart().StartsWith('+') && digits.Length > 0
                            ? $"+{digits}"
                            : digits;
                        guest.PhoneVerifiedAtUtc = null;
                    }
                    await _guestRepo.UpdateAsync(guest);
                }

                await _auditService.LogActionAsync(
                    userId,
                    "PARTIAL_PROFILE_UPDATE",
                    "User",
                    userId.ToString(),
                    newData: new { Fields = updatedFields });

                if (emailChanged && !user.EmailConfirmed)
                {
                    var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                    var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                    var publicAppUrl = _configuration["PublicAppUrl"]
                        ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
                    var verificationUrl = FrontendLinkBuilder.WithFragment(
                        publicAppUrl,
                        "verify-email",
                        new Dictionary<string, string?>
                        {
                            ["userId"] = user.Id.ToString(),
                            ["token"] = encodedToken
                        });
                    await _emailOutbox.EnqueueAsync(
                        TransactionalEmailTemplates.EmailVerification,
                        user.Email!,
                        new EmailVerificationEmail(user.Name, verificationUrl),
                        dataSubjectGuestId: user.GuestId);
                }
            });

            if (emailChanged)
            {
                await _sessionRevocation.RevokeAsync(userId, "ACCOUNT_EMAIL_CHANGED");
            }
        }
    }

    public async Task<IEnumerable<PublicBookingDto>> GetBookingHistoryAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) return Enumerable.Empty<PublicBookingDto>();

        if (string.IsNullOrWhiteSpace(user.GuestId))
        {
            return Enumerable.Empty<PublicBookingDto>();
        }

        var bookings = await _bookingRepo.GetByGuestIdAsync(user.GuestId);
        return bookings
            .Select(b => new PublicBookingDto(
                b.Id, b.BookingCode,
                b.Guest?.FirstName ?? "", b.Guest?.LastName ?? "", b.Guest?.Email ?? "",
                b.CheckIn, b.CheckOut,
                b.Status, b.Amount, b.PaymentStatus, b.PaymentMethod, b.CreatedAt,
                PaymentUrl:
                    b.PaymentMethod == PaymentMethod.Monnify &&
                    b.Status == BookingStatus.Pending &&
                    b.PaymentStatus == PaymentStatus.Unpaid &&
                    b.PaymentCheckoutExpiresAtUtc > DateTime.UtcNow
                        ? b.PaymentCheckoutUrl
                        : null,
                PaymentExpiresAtUtc:
                    b.Status == BookingStatus.Pending &&
                    b.PaymentStatus is PaymentStatus.Unpaid or PaymentStatus.AwaitingVerification
                        ? BookingPaymentPolicy.GetConfirmationDeadlineUtc(b.CreatedAt)
                        : null,
                AdultCount: b.AdultCount,
                ChildCount: b.ChildCount,
                QuoteId: b.QuoteId,
                Currency: b.Currency,
                RoomSubtotal: b.RoomSubtotal,
                DiscountAmount: b.DiscountAmount,
                IncludedTaxAmount: b.IncludedTaxAmount,
                TaxAmount: b.TaxAmount,
                FeeAmount: b.FeeAmount,
                PriceBreakdown: b.Quote?.Lines
                    .OrderBy(line => line.SortOrder)
                    .ThenBy(line => line.Id)
                    .Select(line => new PricingQuoteLineDto(
                        line.Type,
                        line.Code,
                        line.Description,
                        line.StayDate,
                        line.Quantity,
                        line.UnitAmount,
                        line.Amount,
                        line.IsInclusive))
                    .ToArray(),
                RoomTypeId: b.RoomTypeId,
                RoomTypeCode: b.RoomType?.Code,
                RoomTypeName: b.RoomType?.Name,
                RoomQuantity: b.RoomQuantity,
                Rooms: b.ReservationRooms.OrderBy(item => item.Sequence)
                    .Select(item => new PublicReservationRoomDto(
                        item.Id, item.Sequence, item.RoomTypeId,
                        item.RoomTypeCode, item.RoomTypeName))
                    .ToArray(),
                Folio: b.Folio is null ? null : ToFolioSummary(b.Folio),
                ReservationPolicy: new ReservationPolicySnapshotDto(
                    b.ReservationPolicyVersion,
                    b.FreeCancellationHours,
                    b.CancellationPenaltyPercent,
                    b.DepositPercent,
                    b.NoShowPenaltyPercent,
                    FolioAccounting.Money(
                        (b.RoomSubtotal - b.DiscountAmount + b.TaxAmount + b.FeeAmount) *
                        b.DepositPercent / 100m),
                    b.CancellationPenaltyAmount,
                    b.NoShowPenaltyAmount)));
    }

    private static FolioSummaryDto ToFolioSummary(Folio folio)
    {
        var balance = FolioAccounting.Calculate(folio.Entries);
        return new FolioSummaryDto(
            folio.Id, folio.Currency, folio.Status,
            balance.TotalDebits, balance.TotalCredits, balance.Balance,
            balance.AmountDue, balance.GuestCredit, balance.Payments,
            balance.Refunds, folio.OpenedAtUtc, folio.ClosedAtUtc);
    }

    public async Task RotateCredentialsAsync(Guid userId, RotateCredentialsRequest request)
    {
        if (request.NewPassword != request.ConfirmNewPassword)
            throw new BadRequestException("New password and confirmation do not match.");

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new NotFoundException("User not found.");
        if (!await HasValidPasswordAsync(user, request.OldPassword))
            throw new UnauthorizedAccessException("Current password verification failed.");

        await _transaction.ExecuteWithUserLockAsync(userId, async () =>
        {
            var result = await _userManager.ChangePasswordAsync(
                user,
                request.OldPassword,
                request.NewPassword);
            if (!result.Succeeded)
            {
                throw new BadRequestException(
                    string.Join(", ", result.Errors.Select(e => e.Description)));
            }

            await _auditService.LogActionAsync(
                userId,
                "ROTATE_CREDENTIALS",
                "User",
                userId.ToString(),
                new { Message = "Security credentials updated." });
        });
        await _sessionRevocation.RevokeAsync(userId, "CREDENTIALS_ROTATED");
    }

    public async Task DeactivateAccountAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new NotFoundException("User not found.");

        if (user.Role == UserRole.Admin)
            throw new BadRequestException("An administrator account cannot be deactivated.");

        await _transaction.ExecuteWithUserLockAsync(userId, async () =>
        {
            user.Status = ProfileStatus.Suspended;
            user.StatusChangedAtUtc = DateTime.UtcNow;
            var update = await _userManager.UpdateSecurityStampAsync(user);
            if (!update.Succeeded)
            {
                throw new InvalidOperationException("The account could not be deactivated.");
            }

            await _auditService.LogActionAsync(
                userId,
                "DEACTIVATE_ACCOUNT",
                "User",
                userId.ToString(),
                new { Status = "Suspended" });
        });
        await _sessionRevocation.RevokeAsync(userId, "ACCOUNT_DEACTIVATED");
    }

    public async Task ActivateAccountAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new NotFoundException("User not found.");

        await _transaction.ExecuteWithUserLockAsync(userId, async () =>
        {
            user.Status = ProfileStatus.Active;
            user.StatusChangedAtUtc = DateTime.UtcNow;
            var update = await _userManager.UpdateAsync(user);
            if (!update.Succeeded)
            {
                throw new InvalidOperationException("The account could not be activated.");
            }

            await _auditService.LogActionAsync(
                userId,
                "ACTIVATE_ACCOUNT",
                "User",
                userId.ToString(),
                new { Status = "Active" });
        });
    }

    private async Task<bool> HasValidPasswordAsync(
        ApplicationUser user,
        string currentPassword)
    {
        var result = await _signInManager.CheckPasswordSignInAsync(
            user,
            currentPassword,
            lockoutOnFailure: true);
        return result.Succeeded || result.RequiresTwoFactor;
    }

    private static string RequireText(string? value, string field, int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length == 0 || cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }
}
