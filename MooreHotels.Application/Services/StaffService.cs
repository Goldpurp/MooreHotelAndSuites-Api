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
    private readonly IAuditService _auditService;
    private readonly IEmailOutbox _emailOutbox;
    private readonly IApplicationTransaction _transaction;
    private readonly IStaffSessionRevocationService _sessionRevocation;
    private readonly IConfiguration _configuration;

    private static readonly string[] AllowedDepartments = { "Housekeeping", "Reception", "FrontDesk", "Concierge" };

    public StaffService(
        UserManager<ApplicationUser> userManager,
        IAuditService auditService,
        IEmailOutbox emailOutbox,
        IApplicationTransaction transaction,
        IStaffSessionRevocationService sessionRevocation,
        IConfiguration configuration)
    {
        _userManager = userManager;
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
        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        if (actingUser == null) throw new UnauthorizedAccessException("Identity Fault: Acting user context not found.");

        if (request.AssignedRole is UserRole.Admin or UserRole.Client)
            throw new UnauthorizedAccessException("Only Manager and Staff accounts can be provisioned through staff management.");

        // Security Check: Role Hierarchy
        if (actingUser.Role == UserRole.Manager && request.AssignedRole != UserRole.Staff)
            throw new UnauthorizedAccessException("Security Policy: Managers can only onboard 'Staff' roles.");

        if (actingUser.Role != UserRole.Admin && actingUser.Role != UserRole.Manager)
            throw new UnauthorizedAccessException("Permission Denied: Insufficient clearance.");

        // Validation: Departments
        if ((request.AssignedRole == UserRole.Staff || request.AssignedRole == UserRole.Manager) && !string.IsNullOrEmpty(request.Department))
        {
            if (!AllowedDepartments.Contains(request.Department))
                throw new BadRequestException("The selected department is not recognized.");
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var existing = await _userManager.FindByEmailAsync(normalizedEmail);
        if (existing != null) throw new BadRequestException("Email address is already assigned.");

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = normalizedEmail,
            Email = normalizedEmail,
            Name = request.FullName.Trim(),
            PhoneNumber = request.Phone?.Trim(),
            Role = request.AssignedRole,
            Status = ProfileStatus.Active,
            Department = (request.AssignedRole == UserRole.Staff || request.AssignedRole == UserRole.Manager) ? request.Department?.Trim() : null,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
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
                    ["email"] = user.Email,
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
                        new AccountStatusEmail(user.Name));
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

    public async Task UpdateUserAsync(Guid userId, UpdateStaffRequest request, Guid actingUserId)
    {
        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (actingUser is null || actingUser.Status != ProfileStatus.Active ||
            actingUser.Role is not (UserRole.Admin or UserRole.Manager))
            throw new UnauthorizedAccessException("Acting user is not authorized.");
        if (user is null) throw new NotFoundException("Target staff profile was not found.");
        if (user.Role == UserRole.Admin)
            throw new UnauthorizedAccessException("Administrator accounts cannot be edited here.");
        if (request.AssignedRole is UserRole.Admin or UserRole.Client)
            throw new UnauthorizedAccessException("Only Manager and Staff roles can be assigned here.");
        if (actingUser.Role == UserRole.Manager &&
            (user.Role != UserRole.Staff || request.AssignedRole != UserRole.Staff))
            throw new UnauthorizedAccessException("Managers can only edit Staff accounts.");
        if (!string.IsNullOrWhiteSpace(request.Department) &&
            !AllowedDepartments.Contains(request.Department))
            throw new BadRequestException("The selected department is not recognized.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var existing = await _userManager.FindByEmailAsync(normalizedEmail);
        if (existing is not null && existing.Id != userId)
            throw new BadRequestException("Email address is already assigned to another account.");

        var oldRole = user.Role;
        var emailChanged = !string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase);
        await _transaction.ExecuteWithUserLockAsync(userId, async () =>
        {
            user.Name = request.FullName.Trim();
            user.Email = normalizedEmail;
            user.UserName = normalizedEmail;
            user.PhoneNumber = request.Phone?.Trim();
            user.Role = request.AssignedRole;
            user.Department = request.Department?.Trim();

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
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) return;
        if (user.Role == UserRole.Admin) throw new BadRequestException("Administrator accounts are protected.");
        await _transaction.ExecuteWithUserLockAsync(userId, async () =>
        {
            var result = await _userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("The staff account could not be deleted.");
            }
            await _auditService.LogActionAsync(
                actingUserId,
                "USER_DELETED",
                "User",
                userId.ToString(),
                new { Role = user.Role.ToString() });
        });
        await _sessionRevocation.RevokeAsync(userId, "ACCOUNT_DELETED");
    }
}
