using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;
using Microsoft.AspNetCore.RateLimiting;
using MooreHotels.WebAPI.Extensions;
using MooreHotels.Infrastructure.Identity;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/mfa")]
[Authorize]
public sealed class MfaController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IJwtService _jwtService;
    private readonly IConfiguration _configuration;

    public MfaController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IJwtService jwtService,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtService = jwtService;
        _configuration = configuration;
    }

    [HttpPost("setup")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> Setup([FromBody] SetupMfaRequest request)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return Unauthorized();
        if (user.TwoFactorEnabled)
        {
            return Conflict(new { Message = "Two-factor authentication is already enabled." });
        }
        if (!await HasValidPasswordAsync(user, request.CurrentPassword))
            return Unauthorized(new { Message = "Current password verification failed." });

        var key = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key))
        {
            var reset = await _userManager.ResetAuthenticatorKeyAsync(user);
            if (!reset.Succeeded)
            {
                return StatusCode(500, new { Message = "An authenticator key could not be created." });
            }
            key = await _userManager.GetAuthenticatorKeyAsync(user);
        }

        var issuer = _configuration["Jwt:Issuer"] ?? "MooreHotels";
        var account = user.Email ?? user.Id.ToString();
        var authenticatorUri =
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
            $"?secret={Uri.EscapeDataString(key!)}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

        return Ok(new
        {
            SharedKey = key,
            AuthenticatorUri = authenticatorUri,
            AccessToken = _jwtService.GenerateToken(user),
            Message = "Add this account to an authenticator app, then confirm a current code."
        });
    }

    [HttpPost("enable")]
    [EnableRateLimiting(ServiceCollectionExtensions.AuthRateLimitPolicy)]
    public async Task<IActionResult> Enable([FromBody] EnableMfaRequest request)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return Unauthorized();
        if (user.TwoFactorEnabled)
        {
            return Conflict(new { Message = "Two-factor authentication is already enabled." });
        }
        if (!await HasValidPasswordAsync(user, request.CurrentPassword))
            return Unauthorized(new { Message = "Current password verification failed." });

        var code = request.Code
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        var valid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            TokenOptions.DefaultAuthenticatorProvider,
            code);
        if (!valid)
        {
            return BadRequest(new { Message = "The authenticator code is invalid." });
        }

        var enabled = await _userManager.SetTwoFactorEnabledAsync(user, true);
        if (!enabled.Succeeded)
        {
            return StatusCode(500, new { Message = "Two-factor authentication could not be enabled." });
        }

        var recoveryCodes = (await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray() ?? [];
        return Ok(new
        {
            RecoveryCodes = recoveryCodes,
            Message = "Two-factor authentication is enabled. Store these one-time recovery codes securely and sign in again."
        });
    }

    private async Task<ApplicationUser?> GetCurrentUserAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userId, out _)
            ? await _userManager.FindByIdAsync(userId)
            : null;
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
}
