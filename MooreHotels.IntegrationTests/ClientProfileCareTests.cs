using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class ClientProfileCareTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task Correction_preserves_login_identity_and_role_and_clears_changed_phone_verification()
    {
        var client = await fixture.CreateUserAsync(UserRole.Client);
        await fixture.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == client.Id);
            user.PhoneNumber = "+2348000000001";
            user.PhoneNumberConfirmed = true;
            return await db.SaveChangesAsync();
        });
        var before = await fixture.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == client.Id));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/management/clients/{client.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Manager.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        request.Content = JsonContent.Create(new UpdateClientRequest("Corrected Client", "+2348000000002", "QA verified profile correction"));
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await fixture.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == client.Id));
        Assert.Equal("Corrected Client", after.Name);
        Assert.False(after.PhoneNumberConfirmed);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.UserName, after.UserName);
        Assert.Equal(before.GuestId, after.GuestId);
        Assert.Equal(UserRole.Client, after.Role);
        Assert.True(await fixture.WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityId == client.Id.ToString() && a.Action == "CLIENT_PROFILE_UPDATED")));
    }

    [Fact]
    public async Task Non_management_and_staff_targets_are_rejected()
    {
        foreach (var actor in new[] { fixture.Staff, fixture.Admin })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/management/clients/{fixture.Manager.Id}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
            request.Headers.Add("X-Moore-App-Environment", "local");
            request.Content = JsonContent.Create(new UpdateClientRequest("Wrong target", null, "QA forbidden target correction"));
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(actor.Role == UserRole.Admin ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Staff_editor_cannot_promote_a_client_account()
    {
        var client = await fixture.CreateUserAsync(UserRole.Client);
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStaffService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateUserAsync(client.Id,
            new UpdateStaffRequest { FullName = "Wrong promotion", Email = "wrong-promotion@example.test", AssignedRole = UserRole.Staff }, fixture.Admin.Id));
        Assert.Equal(UserRole.Client, await fixture.WithDbAsync(db => db.Users.Where(u => u.Id == client.Id).Select(u => u.Role).SingleAsync()));
    }
}
