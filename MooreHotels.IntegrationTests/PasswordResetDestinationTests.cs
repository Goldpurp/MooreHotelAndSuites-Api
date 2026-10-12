using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class PasswordResetDestinationTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Admin, false)]
    [InlineData(UserRole.Manager, false)]
    [InlineData(UserRole.Staff, false)]
    [InlineData(UserRole.Client, false)]
    public async Task Emailed_reset_targets_the_account_app_and_can_be_consumed_only_once(UserRole role, bool firstPassword)
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Name = "Account recovery regression",
            Email = $"recovery-{Guid.NewGuid():N}@example.test",
            Role = role,
            Status = ProfileStatus.Active,
            EmailConfirmed = true,
            LockoutEnabled = true,
            CreatedAt = DateTime.UtcNow,
            StatusChangedAtUtc = DateTime.UtcNow
        };
        user.UserName = user.Email;
        var created = firstPassword
            ? await users.CreateAsync(user)
            : await users.CreateAsync(user, "PreviousPassword123!");
        Assert.True(created.Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role.ToString())).Succeeded);
        Assert.Equal(!firstPassword, await users.HasPasswordAsync(user));

        using var forgot = await fixture.Client.PostAsJsonAsync("/api/auth/forgot-password", new { user.Email });
        Assert.Equal(HttpStatusCode.OK, forgot.StatusCode);
        var message = await db.EmailOutboxMessages.SingleAsync(m => m.Recipient == user.Email);
        var payload = scope.ServiceProvider.GetRequiredService<IEmailOutbox>().ReadPayload<PasswordResetEmail>(message);
        var link = new Uri(payload.Link);
        var isClient = role == UserRole.Client;
        var expectedBase = new Uri(config[isClient ? "PublicAppUrl" : "DashboardUrl"]!);
        Assert.Equal(expectedBase.GetLeftPart(UriPartial.Authority), link.GetLeftPart(UriPartial.Authority));
        Assert.Equal(isClient ? "/reset-password" : "/", link.AbsolutePath);
        Assert.Equal("", link.Query); // Secrets stay out of server requests and access logs.
        var parameters = QueryHelpers.ParseQuery(link.Fragment.TrimStart('#'));
        if (isClient) Assert.False(parameters.ContainsKey("route"));
        else Assert.Equal("reset-password", parameters["route"].ToString());
        Assert.Equal(user.Id.ToString(), parameters["userId"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(parameters["token"].ToString()));
        var request = new
        {
            UserId = parameters["userId"].ToString(),
            Token = parameters["token"].ToString(),
            NewPassword = "RecoveredPassword123!",
            ConfirmNewPassword = "RecoveredPassword123!"
        };
        using var completed = await fixture.Client.PostAsJsonAsync("/api/auth/reset-password", request);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        await db.Entry(user).ReloadAsync();
        Assert.True(await users.CheckPasswordAsync(user, request.NewPassword));
        using var replay = await fixture.Client.PostAsJsonAsync("/api/auth/reset-password", request);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }
}
