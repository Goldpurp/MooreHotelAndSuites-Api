using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
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
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IJwtService _jwtService;
    private readonly IGuestRepository _guestRepository;
    private readonly IEmailService _emailService;
    private readonly MooreHotelsDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IJwtService jwtService,
        IGuestRepository guestRepository,
        IEmailService emailService,
        MooreHotelsDbContext dbContext,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtService = jwtService;
        _guestRepository = guestRepository;
        _emailService = emailService;
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("login")]
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

        if (!signIn.Succeeded)
        {
            return Unauthorized(new { Message = "Invalid email or password." });
        }

        if (!user.EmailConfirmed)
        {
            return Unauthorized(new { Message = "Please verify your email before signing in." });
        }

        if (user.Status == ProfileStatus.Suspended)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "This account is suspended. Contact hotel administration."
            });
        }

        var token = _jwtService.GenerateToken(user);
        return Ok(new AuthResponse(token, user.Email!, user.Name, user.Role.ToString()));
    }

    [HttpPost("register")]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await _userManager.FindByEmailAsync(email) is not null)
        {
            return Conflict(new { Message = "An account already exists for this email address." });
        }

        var autoConfirm = _configuration.GetValue<bool>("Runtime:AutoConfirmEmail");
        ApplicationUser? user = null;
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
            user = new ApplicationUser
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
                throw new BadRequestException(
                    string.Join(", ", createResult.Errors.Select(error => error.Description)));
            }

            var roleResult = await _userManager.AddToRoleAsync(user, nameof(UserRole.Client));
            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException("The Client role is not initialized.");
            }

            await transaction.CommitAsync();
        });

        if (!autoConfirm)
        {
            await SendVerificationEmailAsync(user!);
        }

        return StatusCode(StatusCodes.Status201Created, new
        {
            Message = autoConfirm
                ? "Account created and activated for the Local environment."
                : "Account created. Check your email to activate it."
        });
    }

    [HttpGet("verify-email")]
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
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> ResendVerification([FromBody] ForgotPasswordRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim().ToLowerInvariant());
        if (user is not null && !user.EmailConfirmed)
        {
            await SendVerificationEmailAsync(user);
        }

        return Ok(new { Message = "If the account requires verification, a new link has been sent." });
    }

    [HttpPost("forgot-password")]
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
                var resetUrl = BuildFrontendUrl("reset-password", new Dictionary<string, string?>
                {
                    ["email"] = user.Email,
                    ["token"] = encodedToken
                });
                await _emailService.SendPasswordResetAsync(user.Email!, user.Name, resetUrl);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Password reset email delivery failed.");
            }
        }

        return Ok(new { Message = "If an eligible account exists, password reset instructions have been sent." });
    }

    [HttpPost("reset-password")]
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

    private async Task SendVerificationEmailAsync(ApplicationUser user)
    {
        try
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var verificationUrl = BuildFrontendUrl("verify-email", new Dictionary<string, string?>
            {
                ["userId"] = user.Id.ToString(),
                ["token"] = encodedToken
            });
            await _emailService.SendEmailVerificationAsync(user.Email!, user.Name, verificationUrl);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Email verification delivery failed after account creation.");
        }
    }

    private string BuildFrontendUrl(string path, IDictionary<string, string?> query)
    {
        var baseUrl = _configuration["PublicAppUrl"]
            ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
        return QueryHelpers.AddQueryString($"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}", query);
    }
}
