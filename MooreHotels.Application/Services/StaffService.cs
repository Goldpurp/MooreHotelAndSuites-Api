using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging; // Added for diagnostic tracking
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
    private readonly IEmailService _emailService;
    private readonly ILogger<StaffService> _logger;
    private readonly IConfiguration _configuration;

    private static readonly string[] AllowedDepartments = { "Housekeeping", "Reception", "FrontDesk", "Concierge" };

    public StaffService(
        UserManager<ApplicationUser> userManager,
        IAuditService auditService,
        IEmailService emailService,
        ILogger<StaffService> logger,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _auditService = auditService;
        _emailService = emailService;
        _logger = logger;
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
        var result = await _userManager.CreateAsync(user, bootstrapPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new BadRequestException(errors);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, request.AssignedRole.ToString());
        if (!roleResult.Succeeded)
        {
            await _userManager.DeleteAsync(user);
            throw new InvalidOperationException("The staff role could not be assigned.");
        }

        try
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var publicAppUrl = _configuration["PublicAppUrl"]
                ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
            var setupLink = QueryHelpers.AddQueryString(
                $"{publicAppUrl.TrimEnd('/')}/reset-password",
                new Dictionary<string, string?>
                {
                    ["email"] = user.Email,
                    ["token"] = encodedToken
                });
            await _emailService.SendStaffWelcomeEmailAsync(user.Email!, user.Name, setupLink, user.Role.ToString());
        }
        catch (Exception ex)
        {
            var cleanup = await _userManager.DeleteAsync(user);
            _logger.LogError(
                ex,
                "Staff setup email failed for user {UserId}; compensation succeeded={CleanupSucceeded}.",
                user.Id,
                cleanup.Succeeded);
            throw new InvalidOperationException("The setup email could not be delivered; no staff account was provisioned.");
        }

        await _auditService.LogActionAsync(
            actingUserId, "USER_PROVISIONED", "User", user.Id.ToString(), null, new
            {
                Role = user.Role.ToString(),
                Department = user.Department,
                Timestamp = user.CreatedAt
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

        if (!Enum.IsDefined(typeof(ProfileStatus), newStatus))
            throw new BadRequestException("Invalid status value.");

        var oldStatus = user.Status;

        if (oldStatus != newStatus)
        {
            user.Status = newStatus;

            if (newStatus == ProfileStatus.Suspended)
                await _userManager.UpdateSecurityStampAsync(user);

            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
                throw new Exception("Failed to update account status.");
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            try
            {
                if (newStatus == ProfileStatus.Active)
                    await _emailService.SendAccountActivatedAsync(user.Email!, user.Name);
                else if (newStatus == ProfileStatus.Suspended)
                    await _emailService.SendAccountSuspendedAsync(user.Email!, user.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send account status email.");
            }
        }

        // Audit log
        await _auditService.LogActionAsync(
            actingUserId,
            "ACCOUNT_STATUS_CHANGED",
            "User",
            user.Id.ToString(),
            new { OldStatus = oldStatus.ToString() },
            new { NewStatus = newStatus.ToString() }
        );
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
        user.Name = request.FullName.Trim();
        user.Email = normalizedEmail;
        user.UserName = normalizedEmail;
        user.PhoneNumber = request.Phone?.Trim();
        user.Role = request.AssignedRole;
        user.Department = request.Department?.Trim();

        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
            throw new BadRequestException(string.Join(", ", update.Errors.Select(error => error.Description)));

        var currentRoles = await _userManager.GetRolesAsync(user);
        if (currentRoles.Count > 0)
        {
            var removeRoles = await _userManager.RemoveFromRolesAsync(user, currentRoles);
            if (!removeRoles.Succeeded) throw new Exception("Staff role synchronization failed.");
        }
        var addRole = await _userManager.AddToRoleAsync(user, request.AssignedRole.ToString());
        if (!addRole.Succeeded) throw new Exception("Staff role synchronization failed.");

        await _userManager.UpdateSecurityStampAsync(user);
        await _auditService.LogActionAsync(
            actingUserId,
            "STAFF_UPDATED",
            "User",
            user.Id.ToString(),
            new { Role = oldRole.ToString() },
            new { EmailChanged = emailChanged, Role = user.Role.ToString(), user.Department });
    }

    public async Task DeleteUserAsync(Guid userId, Guid actingUserId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) return;
        if (user.Role == UserRole.Admin) throw new BadRequestException("Administrator accounts are protected.");
        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded) throw new InvalidOperationException("The staff account could not be deleted.");
        await _auditService.LogActionAsync(
            actingUserId,
            "USER_DELETED",
            "User",
            userId.ToString(),
            new { Role = user.Role.ToString() });
    }
}
