using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.Common;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Identity;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Extensions;
using System.Text;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IJwtService _jwtService;
    private readonly IGuestRepository _guestRepository;
    private readonly IEmailOutbox _emailOutbox;
    private readonly MooreHotelsDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IJwtService jwtService,
        IGuestRepository guestRepository,
        IEmailOutbox emailOutbox,
        MooreHotelsDbContext dbContext,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtService = jwtService;
        _guestRepository = guestRepository;
        _emailOutbox = emailOutbox;
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _userManager.FindByEmailAsync(normalizedEmail);
        if (user is null)
        {
            await Task.Delay(Random.Shared.Next(100, 201));
            return Unauthorized(new { Message = "Invalid email or password." });
        }

        var signIn = await _signInManager.CheckPasswordSignInAsync(
            user,
            request.Password,
            lockoutOnFailure: true);

        if (signIn.IsLockedOut)
        {
            return StatusCode(StatusCodes.Status423Locked, new
            {
                Message = "This account is temporarily locked after repeated failed sign-in attempts. Try again later."
            });
        }

        if (signIn.RequiresTwoFactor)
        {
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
            {
                return Accepted(new
                {
                    RequiresTwoFactor = true,
                    Message = "A two-factor authentication code is required."
                });
            }

            var code = request.TwoFactorCode.Replace(" ", string.Empty, StringComparison.Ordinal);
            var twoFactorIsValid = request.UseRecoveryCode
                ? (await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded
                : await _userManager.VerifyTwoFactorTokenAsync(
                    user,
                    TokenOptions.DefaultAuthenticatorProvider,
                    code.Replace("-", string.Empty, StringComparison.Ordinal));
            if (!twoFactorIsValid)
            {
                await _userManager.AccessFailedAsync(user);
                return Unauthorized(new { Message = "Invalid email, password, or authentication code." });
            }

            await _userManager.ResetAccessFailedCountAsync(user);
        }
        else if (!signIn.Succeeded)
        {
            return Unauthorized(new { Message = "Invalid email or password." });
        }

        if (!user.EmailConfirmed)
        {
            return Unauthorized(new { Message = "Sign-in is unavailable for this account." });
        }

        if (user.Status == ProfileStatus.Suspended)
        {
            return Unauthorized(new { Message = "Sign-in is unavailable for this account." });
        }

        var token = _jwtService.GenerateToken(user);
        var staffMfaRequired = _configuration.GetValue<bool>("Security:RequireStaffMfa") &&
                               user.Role is UserRole.Admin or UserRole.Manager or UserRole.Staff &&
                               !user.TwoFactorEnabled;
        return Ok(new AuthResponse(
            token,
            user.Email!,
            user.Name,
            user.Role.ToString(),
            staffMfaRequired));
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var existingUser = await _userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            // Perform equivalent password-hashing work so the early privacy
            // response is not an obvious fast path for account enumeration.
            _ = _userManager.PasswordHasher.HashPassword(existingUser, request.Password);
            return RegistrationAccepted();
        }

        var autoConfirm = _configuration.GetValue<bool>("Runtime:AutoConfirmEmail");
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();
            var guest = new Guest
            {
                Id = $"GS-{Guid.NewGuid():N}"[..19].ToUpperInvariant(),
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                Email = email,
                Phone = request.Phone.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                Email = email,
                UserName = email,
                Name = $"{request.FirstName.Trim()} {request.LastName.Trim()}",
                Role = UserRole.Client,
                Status = ProfileStatus.Active,
                PhoneNumber = request.Phone.Trim(),
                GuestId = guest.Id,
                EmailConfirmed = autoConfirm,
                LockoutEnabled = true,
                CreatedAt = DateTime.UtcNow
            };

            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            await _guestRepository.AddAsync(guest);

            var createResult = await _userManager.CreateAsync(user, request.Password);
            if (!createResult.Succeeded)
            {
                if (createResult.Errors.Any(error =>
                        error.Code is "DuplicateEmail" or "DuplicateUserName"))
                {
                    // A concurrent request created the account after the first
                    // lookup. Roll back the guest row and keep the public
                    // response indistinguishable from the existing-user case.
                    await transaction.RollbackAsync();
                    return;
                }

                throw new BadRequestException(
                    string.Join(", ", createResult.Errors.Select(error => error.Description)));
            }

            var roleResult = await _userManager.AddToRoleAsync(user, nameof(UserRole.Client));
            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException("The Client role is not initialized.");
            }

            if (!autoConfirm)
            {
                await QueueVerificationEmailAsync(user);
            }

            await transaction.CommitAsync();
        });

        return RegistrationAccepted();
    }

    [HttpGet("verify-email")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.LookupRateLimitPolicy)]
    public async Task<IActionResult> VerifyEmail([FromQuery] string userId, [FromQuery] string token)
    {
        if (!Guid.TryParse(userId, out _) || string.IsNullOrWhiteSpace(token) || token.Length > 4096)
        {
            return BadRequest(new { Message = "The verification link is invalid or expired." });
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return BadRequest(new { Message = "The verification link is invalid or expired." });
        }

        if (user.EmailConfirmed)
        {
            return Ok(new { Message = "Email is already verified." });
        }

        string decodedToken;
        try
        {
            decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            return BadRequest(new { Message = "The verification link is invalid or expired." });
        }

        var result = await _userManager.ConfirmEmailAsync(user, decodedToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { Message = "The verification link is invalid or expired." });
        }

        return Ok(new { Message = "Email verified. You can now sign in." });
    }

    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> ResendVerification([FromBody] ForgotPasswordRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim().ToLowerInvariant());
        if (user is not null && !user.EmailConfirmed)
        {
            try
            {
                await QueueVerificationEmailAsync(user);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Verification email could not be queued.");
            }
        }

        return Ok(new { Message = "If the account requires verification, a new link has been sent." });
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim().ToLowerInvariant());
        if (user is not null && user.EmailConfirmed)
        {
            try
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                var resetUrl = BuildFrontendFragmentUrl("reset-password", new Dictionary<string, string?>
                {
                    ["email"] = user.Email,
                    ["token"] = encodedToken
                });
                await _emailOutbox.EnqueueAsync(
                    TransactionalEmailTemplates.PasswordReset,
                    user.Email!,
                    new PasswordResetEmail(user.Name, resetUrl));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Password reset email delivery failed.");
            }
        }

        return Ok(new { Message = "If an eligible account exists, password reset instructions have been sent." });
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim().ToLowerInvariant());
        if (user is null)
        {
            return BadRequest(new { Message = "The password reset link is invalid or expired." });
        }

        string decodedToken;
        try
        {
            decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
        }
        catch (FormatException)
        {
            return BadRequest(new { Message = "The password reset link is invalid or expired." });
        }

        var result = await _userManager.ResetPasswordAsync(user, decodedToken, request.NewPassword);
        if (!result.Succeeded)
        {
            return BadRequest(new
            {
                Message = "The password reset link is invalid, expired, or the new password is not acceptable.",
                Errors = result.Errors.Select(error => error.Description)
            });
        }

        var signInBaseUrl = user.Role == UserRole.Client
            ? _configuration["PublicAppUrl"]
            : _configuration["DashboardUrl"];
        return Ok(new
        {
            Message = "Password changed. Sign in with your new password.",
            SignInUrl = $"{signInBaseUrl?.TrimEnd('/')}{(user.Role == UserRole.Client ? "/auth" : string.Empty)}"
        });
    }

    private AcceptedResult RegistrationAccepted() => Accepted(new
    {
        Message = "If registration can be completed, activation instructions will be sent."
    });

    private async Task QueueVerificationEmailAsync(ApplicationUser user)
    {
        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var verificationUrl = BuildFrontendFragmentUrl("verify-email", new Dictionary<string, string?>
        {
            ["userId"] = user.Id.ToString(),
            ["token"] = encodedToken
        });
        await _emailOutbox.EnqueueAsync(
            TransactionalEmailTemplates.EmailVerification,
            user.Email!,
            new EmailVerificationEmail(user.Name, verificationUrl));
    }

    private string BuildFrontendFragmentUrl(string path, IDictionary<string, string?> values)
    {
        var baseUrl = _configuration["PublicAppUrl"]
            ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
        return FrontendLinkBuilder.WithFragment(baseUrl, path, values);
    }
}
