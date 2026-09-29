using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;
using System.Text;
using System.Net;
using System.Net.Http.Headers;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class StaffSetupRecoveryTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task Correct_email_then_resend_preserves_staff_and_invalidates_old_link()
    {
        var target = await fixture.CreateUserAsync(UserRole.Staff);
        using var scope = fixture.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var service = scope.ServiceProvider.GetRequiredService<IStaffService>();
        var user = (await manager.FindByIdAsync(target.Id.ToString()))!;
        var oldToken = await manager.GeneratePasswordResetTokenAsync(user);
        var corrected = $"corrected-{Guid.NewGuid():N}@example.test";
        await service.UpdateUserAsync(target.Id, new UpdateStaffRequest
        {
            FullName = "Staff Recovery",
            Email = corrected,
            AssignedRole = UserRole.Staff,
            Department = "Housekeeping"
        }, fixture.Admin.Id);
        await service.ResendSetupAsync(target.Id, fixture.Admin.Id);
        Assert.False(await manager.VerifyUserTokenAsync(user, manager.Options.Tokens.PasswordResetTokenProvider,
            "ResetPassword", oldToken));
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var message = await db.EmailOutboxMessages.SingleAsync(x => x.Recipient == corrected);
        var payload = scope.ServiceProvider.GetRequiredService<IEmailOutbox>().ReadPayload<StaffWelcomeEmail>(message);
        var values = QueryHelpers.ParseQuery(new Uri(payload.SetupLink).Fragment.TrimStart('#'));
        var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(values["token"].ToString()));
        Assert.True(await manager.VerifyUserTokenAsync(user, manager.Options.Tokens.PasswordResetTokenProvider,
            "ResetPassword", token));
        Assert.True((await manager.ResetPasswordAsync(user, token, "RecoveryTest123!")).Succeeded);
        Assert.False(await manager.VerifyUserTokenAsync(user, manager.Options.Tokens.PasswordResetTokenProvider,
            "ResetPassword", token));
        Assert.Equal(UserRole.Staff, user.Role);
        Assert.Equal("Housekeeping", user.Department);
        Assert.Equal(corrected, user.Email);
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "STAFF_SETUP_QUEUED" && x.EntityId == target.Id.ToString()));
    }

    [Fact]
    public async Task Staff_cannot_resend_and_managers_cannot_target_managers()
    {
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStaffService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResendSetupAsync(fixture.Manager.Id, fixture.Staff.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResendSetupAsync(fixture.Manager.Id, fixture.Manager.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResendSetupAsync(fixture.Admin.Id, fixture.Admin.Id));
    }

    [Fact]
    public async Task Http_resend_returns_accepted_and_blocks_staff_callers()
    {
        var target = await fixture.CreateUserAsync(UserRole.Staff);
        foreach (var actor in new[] { fixture.Staff, fixture.Admin })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/admin/management/employees/{target.Id}/resend-setup");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
            request.Headers.Add("X-Moore-App-Environment", "local");
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(actor.Role == UserRole.Admin ? HttpStatusCode.Accepted : HttpStatusCode.Forbidden,
                response.StatusCode);
        }
    }

    [Fact]
    public async Task Suspended_staff_cannot_receive_setup_link()
    {
        var target = await fixture.CreateUserAsync(UserRole.Staff);
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStaffService>();
        await service.ChangeUserStatusAsync(target.Id, ProfileStatus.Suspended, fixture.Admin.Id);
        await Assert.ThrowsAsync<MooreHotels.Application.Exceptions.BadRequestException>(() =>
            service.ResendSetupAsync(target.Id, fixture.Admin.Id));
    }
}
