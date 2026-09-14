using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.WebAPI.Middleware;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class UserStatusMiddlewareTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task Staff_without_mfa_can_rotate_password_but_cannot_open_profile()
    {
        var staff = await fixture.CreateUserAsync(UserRole.Staff);
        await using var scope = fixture.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(staff.Id.ToString());
        Assert.NotNull(user);
        Assert.False(user.TwoFactorEnabled);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RequireStaffMfa"] = "true"
            })
            .Build();

        var rotationReachedEndpoint = false;
        var rotationMiddleware = new UserStatusMiddleware(_ =>
        {
            rotationReachedEndpoint = true;
            return Task.CompletedTask;
        });
        var rotationContext = Context(user, HttpMethods.Post, "/api/profile/rotate-security");

        await rotationMiddleware.InvokeAsync(rotationContext, userManager, configuration);

        Assert.True(rotationReachedEndpoint);

        var profileReachedEndpoint = false;
        var profileMiddleware = new UserStatusMiddleware(_ =>
        {
            profileReachedEndpoint = true;
            return Task.CompletedTask;
        });
        var profileContext = Context(user, HttpMethods.Get, "/api/profile/me");

        await profileMiddleware.InvokeAsync(profileContext, userManager, configuration);

        Assert.False(profileReachedEndpoint);
        Assert.Equal(StatusCodes.Status403Forbidden, profileContext.Response.StatusCode);
    }

    private static DefaultHttpContext Context(
        ApplicationUser user,
        string method,
        string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("security_stamp", user.SecurityStamp!)
        ], "test"));
        return context;
    }
}
