using Microsoft.AspNetCore.Identity;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using System.Security.Claims;

namespace MooreHotels.WebAPI.Middleware;

public class UserStatusMiddleware
{
    private readonly RequestDelegate _next;

    public UserStatusMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, UserManager<ApplicationUser> userManager)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userIdStr = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(userIdStr, out var userId))
            {
                var user = await userManager.FindByIdAsync(userIdStr);

                var tokenStamp = context.User.FindFirstValue("security_stamp");
                var hasValidSecurityStamp = user is not null &&
                    !string.IsNullOrEmpty(tokenStamp) &&
                    string.Equals(tokenStamp, user.SecurityStamp, StringComparison.Ordinal);

                if (user == null || user.Status == ProfileStatus.Suspended || !hasValidSecurityStamp)
                {
                    var errorCode = user?.Status == ProfileStatus.Suspended
                        ? "ACCOUNT_SUSPENDED"
                        : "SESSION_REVOKED";
                    var message = user?.Status == ProfileStatus.Suspended
                        ? "This account is suspended. Contact an administrator."
                        : "This session is no longer valid. Please sign in again.";
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        Message = message,
                        ErrorCode = errorCode
                    });
                    return;
                }
            }
        }

        await _next(context);
    }
}

public static class UserStatusMiddlewareExtensions
{
    public static IApplicationBuilder UseUserStatusEnforcement(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<UserStatusMiddleware>();
    }
}
