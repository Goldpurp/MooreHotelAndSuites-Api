using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using System.Security.Cryptography;
using System.Text;

namespace MooreHotels.Application.Services;

public class StaffService : IStaffService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IAuditService _auditService;
    private readonly IEmailOutbox _emailOutbox;
    private readonly IApplicationTransaction _transaction;
    private readonly IStaffSessionRevocationService _sessionRevocation;
    private readonly IConfiguration _configuration;

    private static readonly string[] AllowedDepartments =
    {
        "Housekeeping", "Maintenance", "Engineering", "Reception", "FrontDesk",
        "Concierge", "Finance", "Cashier"
    };

    public StaffService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAuditService auditService,
        IEmailOutbox emailOutbox,
        IApplicationTransaction transaction,
        IStaffSessionRevocationService sessionRevocation,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _auditService = auditService;
        _emailOutbox = emailOutbox;
        _transaction = transaction;
        _sessionRevocation = sessionRevocation;
        _configuration = configuration;
    }

    public Task<StaffDashboardStatsDto> GetStaffStatsAsync()
    {
        var users = _userManager.Users.ToList();
        var stats = new StaffDashboardStatsDto(
            ActiveAccounts: users.Count(u => u.Status == ProfileStatus.Active && u.Role != UserRole.Client),
            TotalStaffCount: users.Count(u => u.Role != UserRole.Client),
            AccessSuspended: users.Count(u => u.Status == ProfileStatus.Suspended && u.Role != UserRole.Client)
        );
        return Task.FromResult(stats);
    }

    public Task<IEnumerable<StaffSummaryDto>> GetAllStaffAsync()
    {
        var staff = _userManager.Users
            .Where(u => u.Role == UserRole.Admin || u.Role == UserRole.Manager || u.Role == UserRole.Staff)
            .OrderByDescending(u => u.CreatedAt)
            .ToList()
            .Select(u => new StaffSummaryDto(
                u.Id,
                u.Name,
                u.Email!,
                u.PhoneNumber,
                u.AvatarUrl,
                u.Role,
                u.Department,
                u.CreatedAt,
                u.Status
            ));
        return Task.FromResult(staff);
    }

    public Task<IEnumerable<StaffSummaryDto>> GetAllUsersAsync()
    {
        var users = _userManager.Users
            .OrderByDescending(u => u.CreatedAt)
            .ToList()
            .Select(u => new StaffSummaryDto(
                u.Id,
                u.Name,
                u.Email!,
                u.PhoneNumber,
                u.AvatarUrl,
                u.Role,
                u.Department,
                u.CreatedAt,
                u.Status
      ));
        return Task.FromResult(users);
    }

    public async Task OnboardUserAsync(OnboardUserRequest request, Guid actingUserId)
    {
        RequireActor(actingUserId);
        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        if (actingUser == null || actingUser.Status != ProfileStatus.Active)
            throw new UnauthorizedAccessException("Identity Fault: Acting user context not found.");

        if (!Enum.IsDefined(request.AssignedRole) || !Enum.IsDefined(request.Status))
            throw new BadRequestException("The requested account role or status is invalid.");
        if (request.Status != ProfileStatus.Active)
            throw new BadRequestException("New staff accounts must be onboarded as active.");

        if (request.AssignedRole is UserRole.Admin or UserRole.Client)
            throw new UnauthorizedAccessException("Only Manager and Staff accounts can be provisioned through staff management.");

        // Security Check: Role Hierarchy
        if (actingUser.Role == UserRole.Manager && request.AssignedRole != UserRole.Staff)
            throw new UnauthorizedAccessException("Security Policy: Managers can only onboard 'Staff' roles.");

        if (actingUser.Role != UserRole.Admin && actingUser.Role != UserRole.Manager)
            throw new UnauthorizedAccessException("Permission Denied: Insufficient clearance.");

        // Validation: Departments
        var fullName = RequireText(request.FullName, "Full name", 160);
        var normalizedEmail = RequireEmail(request.Email);
        var phone = NormalizePhone(request.Phone);
        var department = NormalizeDepartment(request.Department);
        if ((request.AssignedRole == UserRole.Staff || request.AssignedRole == UserRole.Manager) && department is not null)
        {
            if (!AllowedDepartments.Contains(department, StringComparer.Ordinal))
                throw new BadRequestException("The selected department is not recognized.");
        }

        var existing = await _userManager.FindByEmailAsync(normalizedEmail);
        if (existing != null) throw new BadRequestException("Email address is already assigned.");

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = normalizedEmail,
            Email = normalizedEmail,
            Name = fullName,
            PhoneNumber = phone,
            Role = request.AssignedRole,
            Status = ProfileStatus.Active,
            Department = department,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            StatusChangedAtUtc = DateTime.UtcNow
        };

        // The administrator never chooses or transmits the staff member's
        // credential. A random bootstrap password is immediately superseded by
        // the single-use setup link sent to the staff member.
        var bootstrapPassword = $"Aa1!{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24))}";
        await _transaction.ExecuteAsync(async () =>
        {
            var result = await _userManager.CreateAsync(user, bootstrapPassword);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                throw new BadRequestException(errors);
            }

            var roleResult = await _userManager.AddToRoleAsync(user, request.AssignedRole.ToString());
            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException("The staff role could not be assigned.");
            }

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var publicAppUrl = _configuration["PublicAppUrl"]
                ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
            var setupLink = FrontendLinkBuilder.WithFragment(
                publicAppUrl,
                "reset-password",
                new Dictionary<string, string?>
                {
                    ["userId"] = user.Id.ToString(),
                    ["token"] = encodedToken
                });
            await _emailOutbox.EnqueueAsync(
                TransactionalEmailTemplates.StaffWelcome,
                user.Email!,
                new StaffWelcomeEmail(user.Name, setupLink, user.Role.ToString()));

            await _auditService.LogActionAsync(
                actingUserId, "USER_PROVISIONED", "User", user.Id.ToString(), null, new
                {
                    Role = user.Role.ToString(),
                    Department = user.Department,
                    Timestamp = user.CreatedAt
                });
        });
    }

    public async Task ChangeUserStatusAsync(
        Guid userId,
        ProfileStatus newStatus,
        Guid actingUserId)
    {
        RequireActor(actingUserId);
        if (userId == Guid.Empty) throw new NotFoundException("Target user profile not found.");
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
            throw new NotFoundException("Target user profile not found.");

        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        if (actingUser is null || actingUser.Status != ProfileStatus.Active)
            throw new UnauthorizedAccessException("Acting user is not authorized.");

        if (actingUser.Role == UserRole.Manager && user.Role != UserRole.Staff)
            throw new UnauthorizedAccessException("Managers can only change Staff account status.");

        if (actingUser.Role is not (UserRole.Admin or UserRole.Manager))
            throw new UnauthorizedAccessException("Acting user is not authorized.");

        if (user.Role == UserRole.Admin)
            throw new BadRequestException("Administrator accounts cannot be modified here.");

        if (!Enum.IsDefined(newStatus))
            throw new BadRequestException("Invalid status value.");

        var oldStatus = user.Status;

        if (oldStatus != newStatus)
        {
            await _transaction.ExecuteWithUserLockAsync(userId, async () =>
            {
                user.Status = newStatus;
                user.StatusChangedAtUtc = DateTime.UtcNow;
                IdentityResult updateResult;
                if (newStatus == ProfileStatus.Suspended)
                {
                    updateResult = await _userManager.UpdateSecurityStampAsync(user);
                }
                else
                {
                    updateResult = await _userManager.UpdateAsync(user);
                }
                if (!updateResult.Succeeded)
                {
                    throw new InvalidOperationException("Failed to update account status.");
                }

                if (!string.IsNullOrWhiteSpace(user.Email))
                {
                    var template = newStatus == ProfileStatus.Active
                        ? TransactionalEmailTemplates.AccountActivated
                        : TransactionalEmailTemplates.AccountSuspended;
                    await _emailOutbox.EnqueueAsync(
                        template,
                        user.Email,
                        new AccountStatusEmail(user.Name),
                        dataSubjectGuestId: user.GuestId);
                }

                await _auditService.LogActionAsync(
                    actingUserId,
                    "ACCOUNT_STATUS_CHANGED",
                    "User",
                    user.Id.ToString(),
                    new { OldStatus = oldStatus.ToString() },
                    new { NewStatus = newStatus.ToString() });
            });

            if (newStatus == ProfileStatus.Suspended)
            {
                await _sessionRevocation.RevokeAsync(userId, "ACCOUNT_SUSPENDED");
            }
        }
    }

    public async Task ChangeAdministratorStatusAsync(
        Guid userId,
        ProfileStatus newStatus,
        EmergencyAdminStatusRequest request,
        Guid actingUserId)
    {
        RequireActor(actingUserId);
        if (userId == Guid.Empty) throw new NotFoundException("Target administrator profile not found.");
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || request.CurrentPassword.Length > 128 ||
            string.IsNullOrWhiteSpace(request.AuthenticatorCode) || request.AuthenticatorCode.Length > 16 ||
            string.IsNullOrWhiteSpace(request.Confirmation) || request.Confirmation.Length > 320 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length is < 10 or > 500)
        {
            throw new BadRequestException("Emergency administrator confirmation details are invalid.");
        }
        if (newStatus is not (ProfileStatus.Active or ProfileStatus.Suspended))
            throw new BadRequestException("Invalid administrator status value.");
        if (userId == actingUserId)
            throw new BadRequestException("Administrators cannot change their own emergency status.");

        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        if (actingUser is null ||
            actingUser.Role != UserRole.Admin ||
            actingUser.Status != ProfileStatus.Active ||
            !actingUser.EmailConfirmed ||
            !actingUser.TwoFactorEnabled)
        {
            throw new UnauthorizedAccessException(
                "An active, verified administrator with MFA is required.");
        }

        var passwordCheck = await _signInManager.CheckPasswordSignInAsync(
            actingUser,
            request.CurrentPassword,
            lockoutOnFailure: true);
        if (!passwordCheck.Succeeded && !passwordCheck.RequiresTwoFactor)
            throw new UnauthorizedAccessException("Step-up authentication failed.");

        var authenticatorCode = request.AuthenticatorCode
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (!await _userManager.VerifyTwoFactorTokenAsync(
                actingUser,
                TokenOptions.DefaultAuthenticatorProvider,
                authenticatorCode))
        {
            await _userManager.AccessFailedAsync(actingUser);
            throw new UnauthorizedAccessException("Step-up authentication failed.");
        }
        await _userManager.ResetAccessFailedCountAsync(actingUser);

        var reason = request.Reason.Trim();
        if (reason.Any(char.IsControl))
            throw new BadRequestException("The reason cannot contain control characters.");

        var statusChanged = false;
        await _transaction.ExecuteWithAdminStatusLockAsync(async () =>
        {
            var currentActor = await _userManager.FindByIdAsync(actingUserId.ToString());
            var target = await _userManager.FindByIdAsync(userId.ToString());
            if (currentActor is null ||
                currentActor.Role != UserRole.Admin ||
                currentActor.Status != ProfileStatus.Active ||
                !currentActor.EmailConfirmed ||
                !currentActor.TwoFactorEnabled)
            {
                throw new UnauthorizedAccessException("Acting administrator is no longer authorized.");
            }
            if (target is null)
                throw new NotFoundException("Target administrator profile not found.");
            if (target.Role != UserRole.Admin)
                throw new BadRequestException("The emergency workflow is only for administrator accounts.");

            var action = newStatus == ProfileStatus.Suspended ? "SUSPEND" : "REACTIVATE";
            var expectedConfirmation = $"{action} {target.Email}";
            if (!string.Equals(
                    request.Confirmation.Trim(),
                    expectedConfirmation,
                    StringComparison.Ordinal))
            {
                throw new BadRequestException(
                    $"Type '{expectedConfirmation}' to confirm this emergency action.");
            }
            if (target.Status == newStatus)
                throw new BadRequestException($"The administrator account is already {newStatus.ToString().ToLowerInvariant()}.");

            var remainingActiveAdministrators = _userManager.Users.Count(user =>
                user.Id != target.Id &&
                user.Role == UserRole.Admin &&
                user.Status == ProfileStatus.Active &&
                user.EmailConfirmed &&
                user.TwoFactorEnabled);
            if (newStatus == ProfileStatus.Suspended && remainingActiveAdministrators < 1)
            {
                throw new BadRequestException(
                    "The last operational administrator cannot be suspended.");
            }

            var oldStatus = target.Status;
            target.Status = newStatus;
            target.StatusChangedAtUtc = DateTime.UtcNow;
            var update = await _userManager.UpdateSecurityStampAsync(target);
            if (!update.Succeeded)
                throw new InvalidOperationException("The administrator status could not be changed.");

            if (!string.IsNullOrWhiteSpace(target.Email))
            {
                var template = newStatus == ProfileStatus.Active
                    ? TransactionalEmailTemplates.AccountActivated
                    : TransactionalEmailTemplates.AccountSuspended;
                await _emailOutbox.EnqueueAsync(
                    template,
                    target.Email,
                    new AccountStatusEmail(target.Name),
                    dataSubjectGuestId: target.GuestId);
            }

            await _auditService.LogActionAsync(
                actingUserId,
                newStatus == ProfileStatus.Suspended
                    ? "EMERGENCY_ADMIN_SUSPENDED"
                    : "EMERGENCY_ADMIN_REACTIVATED",
                "User",
                target.Id.ToString(),
                new { Status = oldStatus.ToString() },
                new
                {
                    Status = newStatus.ToString(),
                    Reason = reason,
                    StepUpMethod = "PasswordAndTotp",
                    RemainingOperationalAdministrators = remainingActiveAdministrators,
                    OccurredAtUtc = DateTime.UtcNow
                });
            statusChanged = true;
        });

        if (statusChanged)
        {
            await _sessionRevocation.RevokeAsync(
                userId,
                newStatus == ProfileStatus.Suspended
                    ? "EMERGENCY_ADMIN_SUSPENDED"
                    : "EMERGENCY_ADMIN_REACTIVATED");
        }
    }

    public async Task UpdateUserAsync(Guid userId, UpdateStaffRequest request, Guid actingUserId)
    {
        RequireActor(actingUserId);
        if (userId == Guid.Empty) throw new NotFoundException("Target staff profile was not found.");
        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (actingUser is null || actingUser.Status != ProfileStatus.Active ||
            actingUser.Role is not (UserRole.Admin or UserRole.Manager))
            throw new UnauthorizedAccessException("Acting user is not authorized.");
        if (user is null) throw new NotFoundException("Target staff profile was not found.");
        if (user.Role == UserRole.Admin)
            throw new UnauthorizedAccessException("Administrator accounts cannot be edited here.");
        if (!Enum.IsDefined(request.AssignedRole) || request.AssignedRole is UserRole.Admin or UserRole.Client)
            throw new UnauthorizedAccessException("Only Manager and Staff roles can be assigned here.");
        if (actingUser.Role == UserRole.Manager &&
            (user.Role != UserRole.Staff || request.AssignedRole != UserRole.Staff))
            throw new UnauthorizedAccessException("Managers can only edit Staff accounts.");
        var fullName = RequireText(request.FullName, "Full name", 160);
        var normalizedEmail = RequireEmail(request.Email);
        var phone = NormalizePhone(request.Phone);
        var department = NormalizeDepartment(request.Department);
        if (department is not null &&
            !AllowedDepartments.Contains(department, StringComparer.Ordinal))
            throw new BadRequestException("The selected department is not recognized.");

        var existing = await _userManager.FindByEmailAsync(normalizedEmail);
        if (existing is not null && existing.Id != userId)
            throw new BadRequestException("Email address is already assigned to another account.");

        var oldRole = user.Role;
        var emailChanged = !string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase);
        await _transaction.ExecuteWithUserLockAsync(userId, async () =>
        {
            user.Name = fullName;
            user.Email = normalizedEmail;
            user.UserName = normalizedEmail;
            user.PhoneNumber = phone;
            user.Role = request.AssignedRole;
            user.Department = department;

            var update = await _userManager.UpdateAsync(user);
            if (!update.Succeeded)
            {
                throw new BadRequestException(
                    string.Join(", ", update.Errors.Select(error => error.Description)));
            }

            var currentRoles = await _userManager.GetRolesAsync(user);
            if (currentRoles.Count > 0)
            {
                var removeRoles = await _userManager.RemoveFromRolesAsync(user, currentRoles);
                if (!removeRoles.Succeeded)
                {
                    throw new InvalidOperationException("Staff role synchronization failed.");
                }
            }
            var addRole = await _userManager.AddToRoleAsync(user, request.AssignedRole.ToString());
            if (!addRole.Succeeded)
            {
                throw new InvalidOperationException("Staff role synchronization failed.");
            }

            var securityStamp = await _userManager.UpdateSecurityStampAsync(user);
            if (!securityStamp.Succeeded)
            {
                throw new InvalidOperationException("Staff sessions could not be invalidated.");
            }
            await _auditService.LogActionAsync(
                actingUserId,
                "STAFF_UPDATED",
                "User",
                user.Id.ToString(),
                new { Role = oldRole.ToString() },
                new { EmailChanged = emailChanged, Role = user.Role.ToString(), user.Department });
        });
        await _sessionRevocation.RevokeAsync(userId, "STAFF_PROFILE_CHANGED");
    }

    public async Task DeleteUserAsync(Guid userId, Guid actingUserId)
    {
        RequireActor(actingUserId);
        if (userId == Guid.Empty) throw new NotFoundException("Target staff profile was not found.");
        if (userId == actingUserId)
            throw new BadRequestException("Administrators cannot decommission their own account.");
        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        if (actingUser is null || actingUser.Role != UserRole.Admin ||
            actingUser.Status != ProfileStatus.Active)
        {
            throw new UnauthorizedAccessException("An active administrator is required.");
        }
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) return;
        if (user.Role == UserRole.Admin) throw new BadRequestException("Administrator accounts are protected.");
        await _transaction.ExecuteWithUserLockAsync(userId, async () =>
        {
            var oldRole = user.Role;
            var tombstoneEmail = $"decommissioned-{user.Id:N}@invalid.moorehotels.local";
            user.Name = "Decommissioned staff account";
            user.Email = tombstoneEmail;
            user.UserName = tombstoneEmail;
            user.PhoneNumber = null;
            user.Department = null;
            user.EmailConfirmed = false;
            user.PhoneNumberConfirmed = false;
            user.TwoFactorEnabled = false;
            user.Status = ProfileStatus.Suspended;
            user.StatusChangedAtUtc = DateTime.UtcNow;
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.MaxValue;
            var result = await _userManager.UpdateSecurityStampAsync(user);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("The staff account could not be decommissioned.");
            }
            await _auditService.LogActionAsync(
                actingUserId,
                "USER_DECOMMISSIONED",
                "User",
                userId.ToString(),
                new { Role = oldRole.ToString() },
                new { Status = ProfileStatus.Suspended.ToString(), PersonalDataRemoved = true });
        });
        await _sessionRevocation.RevokeAsync(userId, "ACCOUNT_DECOMMISSIONED");
    }

    private static void RequireActor(Guid actorId)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated actor is invalid.");
    }

    private static string RequireText(string? value, string field, int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length < 2 || cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }

    private static string RequireEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (email.Length == 0 || email.Length > 254 || email.Any(char.IsControl) ||
            !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
        {
            throw new BadRequestException("Email address is invalid.");
        }
        return email;
    }

    private static string? NormalizePhone(string? value)
    {
        if (value is null) return null;
        var phone = value.Trim();
        if (phone.Length > 30 || phone.Any(char.IsControl) ||
            (phone.Length > 0 &&
             !new System.ComponentModel.DataAnnotations.PhoneAttribute().IsValid(phone)))
        {
            throw new BadRequestException("Phone number is invalid.");
        }
        return phone.Length == 0 ? null : phone;
    }

    private static string? NormalizeDepartment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var department = value.Trim();
        if (department.Length > 80 || department.Any(char.IsControl))
            throw new BadRequestException("The selected department is invalid.");
        return department;
    }
}
