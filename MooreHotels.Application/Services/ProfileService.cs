using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Exceptions;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Domain.Common;
using System.Text;

namespace MooreHotels.Application.Services;

public class ProfileService : IProfileService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _auditService;
    private readonly IBookingRepository _bookingRepo;
    private readonly IGuestRepository _guestRepo;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;

    public ProfileService(
        UserManager<ApplicationUser> userManager, 
        IAuditService auditService, 
        IBookingRepository bookingRepo,
        IGuestRepository guestRepo,
        IEmailService emailService,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _auditService = auditService;
        _bookingRepo = bookingRepo;
        _guestRepo = guestRepo;
        _emailService = emailService;
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
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new NotFoundException("User account not found.");

        bool isChanged = false;
        bool emailChanged = false;
        var updatedFields = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            user.Name = request.FullName.Trim();
            isChanged = true;
            updatedFields.Add("name");
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && request.Email != user.Email)
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
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

        if (request.Phone != null)
        {
            user.PhoneNumber = request.Phone;
            isChanged = true;
            updatedFields.Add("phone");
        }

        if (request.AvatarUrl != null)
        {
            if (!IsTrustedAvatarUrl(request.AvatarUrl))
            {
                throw new BadRequestException("Avatar images must be uploaded through the Moore Hotels image service.");
            }
            user.AvatarUrl = request.AvatarUrl;
            isChanged = true;
            updatedFields.Add("avatar");
        }

        if (isChanged)
        {
            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded) 
                throw new BadRequestException(string.Join(", ", result.Errors.Select(e => e.Description)));

            if (emailChanged)
            {
                await _userManager.UpdateSecurityStampAsync(user);
            }

            var guest = string.IsNullOrWhiteSpace(user.GuestId)
                ? null
                : await _guestRepo.GetByIdAsync(user.GuestId);
            if (guest != null)
            {
                if (!string.IsNullOrWhiteSpace(request.FullName))
                {
                    var names = request.FullName.Split(' ', 2);
                    guest.FirstName = names[0];
                    guest.LastName = names.Length > 1 ? names[1] : "";
                }
                
                if (emailChanged) guest.Email = user.Email!;
                if (request.Phone != null) guest.Phone = request.Phone;
                if (request.AvatarUrl != null) guest.AvatarUrl = request.AvatarUrl;
                
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
                var verificationUrl = QueryHelpers.AddQueryString(
                    $"{publicAppUrl.TrimEnd('/')}/verify-email",
                    new Dictionary<string, string?>
                    {
                        ["userId"] = user.Id.ToString(),
                        ["token"] = encodedToken
                    });
                await _emailService.SendEmailVerificationAsync(user.Email!, user.Name, verificationUrl);
            }
        }
    }

    public async Task<IEnumerable<BookingDto>> GetBookingHistoryAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) return Enumerable.Empty<BookingDto>();

        if (string.IsNullOrWhiteSpace(user.GuestId))
        {
            return Enumerable.Empty<BookingDto>();
        }

        var bookings = await _bookingRepo.GetByGuestIdAsync(user.GuestId);
        return bookings
            .Select(b => new BookingDto(
                b.Id, b.BookingCode, b.RoomId, b.GuestId, 
                b.Guest?.FirstName ?? "", b.Guest?.LastName ?? "", b.Guest?.Email ?? "", b.Guest?.Phone ?? "",
                b.CheckIn, b.CheckOut,
                b.Status, b.Amount, b.PaymentStatus, b.PaymentMethod, b.TransactionReference, b.Notes, b.CreatedAt,
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
                        : null));
    }

    public async Task RotateCredentialsAsync(Guid userId, RotateCredentialsRequest request)
    {
        if (request.NewPassword != request.ConfirmNewPassword)
            throw new BadRequestException("New password and confirmation do not match.");

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new NotFoundException("User not found.");

        var result = await _userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);
        if (!result.Succeeded) 
            throw new BadRequestException(string.Join(", ", result.Errors.Select(e => e.Description)));

        await _auditService.LogActionAsync(userId, "ROTATE_CREDENTIALS", "User", userId.ToString(), 
            new { Message = "Security credentials updated." });
    }

    public async Task DeactivateAccountAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new Exception("User not found");

        if (user.Role == UserRole.Admin)
            throw new Exception("Security Constraint: Admin account cannot be deactivated.");

        user.Status = ProfileStatus.Suspended;
        await _userManager.UpdateAsync(user);
        await _userManager.UpdateSecurityStampAsync(user);

        await _auditService.LogActionAsync(userId, "DEACTIVATE_ACCOUNT", "User", userId.ToString(), 
            new { Status = "Suspended" });
    }

    public async Task ActivateAccountAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) throw new Exception("User not found");

        user.Status = ProfileStatus.Active;
        await _userManager.UpdateAsync(user);

        await _auditService.LogActionAsync(userId, "ACTIVATE_ACCOUNT", "User", userId.ToString(), 
            new { Status = "Active" });
    }

    private bool IsTrustedAvatarUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("res.cloudinary.com", StringComparison.OrdinalIgnoreCase))
        {
            var cloudName = _configuration["Cloudinary:CloudName"];
            return !string.IsNullOrWhiteSpace(cloudName) &&
                   uri.AbsolutePath.StartsWith(
                       $"/{cloudName}/image/upload/",
                       StringComparison.Ordinal);
        }

        var apiBaseUrl = _configuration["Api:PublicBaseUrl"];
        return Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiUri) &&
               uri.Scheme == apiUri.Scheme &&
               uri.Host.Equals(apiUri.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == apiUri.Port &&
               uri.AbsolutePath.StartsWith("/uploads/avatars/", StringComparison.Ordinal);
    }

}
