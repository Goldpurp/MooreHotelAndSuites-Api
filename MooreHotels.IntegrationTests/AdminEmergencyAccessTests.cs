using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Hubs;
using MooreHotels.Infrastructure.Identity;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class AdminEmergencyAccessTests
{
    private const string TestPassword = "TransferTest123!";
    private readonly ManualTransferTestFixture _fixture;

    public AdminEmergencyAccessTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Emergency_admin_status_change_requires_step_up_and_revokes_existing_access()
    {
        var actor = await EnableMfaAndRefreshTokenAsync(
            await _fixture.CreateUserAsync(UserRole.Admin));
        var target = await EnableMfaAndRefreshTokenAsync(
            await _fixture.CreateUserAsync(UserRole.Admin));
        var targetState = await GetMfaStateAsync(target.User.Id);
        var registry = _fixture.Services.GetRequiredService<StaffConnectionRegistry>();
        Assert.True(await registry.TryRegisterAsync(
            target.User.Id,
            "emergency-admin-target",
            targetState.SecurityStamp,
            new RecordingGroupManager()));

        try
        {
            using (var wrongPassword = EmergencyRequest(
                       target.User.Id,
                       "emergency-suspend-admin",
                       actor.User,
                       "incorrect-password",
                       actor.Code,
                       $"SUSPEND {targetState.Email}"))
            using (var rejected = await _fixture.Client.SendAsync(wrongPassword))
                Assert.True(
                    rejected.StatusCode == HttpStatusCode.Forbidden,
                    $"Expected Forbidden, received {rejected.StatusCode}: {await rejected.Content.ReadAsStringAsync()}");

            Assert.Equal(
                ProfileStatus.Active,
                await _fixture.WithDbAsync(async db =>
                    (await db.Users.AsNoTracking().SingleAsync(user => user.Id == target.User.Id)).Status));

            using (var suspend = EmergencyRequest(
                       target.User.Id,
                       "emergency-suspend-admin",
                       actor.User,
                       TestPassword,
                       actor.Code,
                       $"SUSPEND {targetState.Email}"))
            using (var suspended = await _fixture.Client.SendAsync(suspend))
                Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

            Assert.Equal(0, registry.GetActiveConnectionCount(target.User.Id));
            using (var staleTargetRequest = Authorized(
                       HttpMethod.Get,
                       "/api/admin/management/stats",
                       target.User))
            using (var staleTargetResponse = await _fixture.Client.SendAsync(staleTargetRequest))
                Assert.Equal(HttpStatusCode.Forbidden, staleTargetResponse.StatusCode);

            using (var reactivate = EmergencyRequest(
                       target.User.Id,
                       "emergency-reactivate-admin",
                       actor.User,
                       TestPassword,
                       actor.Code,
                       $"REACTIVATE {targetState.Email}"))
            using (var reactivated = await _fixture.Client.SendAsync(reactivate))
                Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);

            var evidence = await _fixture.WithDbAsync(async db => new
            {
                Target = await db.Users.AsNoTracking().SingleAsync(user => user.Id == target.User.Id),
                Actions = await db.AuditLogs.AsNoTracking()
                    .Where(log => log.EntityType == "User" && log.EntityId == target.User.Id.ToString())
                    .Select(log => log.Action)
                    .ToListAsync()
            });
            Assert.Equal(ProfileStatus.Active, evidence.Target.Status);
            Assert.NotEqual(targetState.SecurityStamp, evidence.Target.SecurityStamp);
            Assert.Contains("EMERGENCY_ADMIN_SUSPENDED", evidence.Actions);
            Assert.Contains("EMERGENCY_ADMIN_REACTIVATED", evidence.Actions);

            using var selfSuspend = EmergencyRequest(
                actor.User.Id,
                "emergency-suspend-admin",
                actor.User,
                TestPassword,
                actor.Code,
                $"SUSPEND {actor.Email}");
            using var selfSuspendResponse = await _fixture.Client.SendAsync(selfSuspend);
            Assert.Equal(HttpStatusCode.BadRequest, selfSuspendResponse.StatusCode);
        }
        finally
        {
            await DeleteUserAsync(target.User.Id);
            await DeleteUserAsync(actor.User.Id);
        }
    }

    [Fact]
    public async Task Concurrent_mutual_suspension_preserves_one_operational_administrator()
    {
        var first = await EnableMfaAndRefreshTokenAsync(
            await _fixture.CreateUserAsync(UserRole.Admin));
        var second = await EnableMfaAndRefreshTokenAsync(
            await _fixture.CreateUserAsync(UserRole.Admin));
        Dictionary<Guid, ProfileStatus> otherAdministratorStatuses = [];

        try
        {
            otherAdministratorStatuses = await _fixture.WithDbAsync(async db =>
            {
                var others = await db.Users
                    .Where(user => user.Role == UserRole.Admin &&
                                   user.Id != first.User.Id &&
                                   user.Id != second.User.Id)
                    .ToDictionaryAsync(user => user.Id, user => user.Status);
                await db.Users
                    .Where(user => user.Role == UserRole.Admin &&
                                   user.Id != first.User.Id &&
                                   user.Id != second.User.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        user => user.Status,
                        ProfileStatus.Suspended));
                return others;
            });

            using var firstRequest = EmergencyRequest(
                second.User.Id,
                "emergency-suspend-admin",
                first.User,
                TestPassword,
                first.Code,
                $"SUSPEND {second.Email}");
            using var secondRequest = EmergencyRequest(
                first.User.Id,
                "emergency-suspend-admin",
                second.User,
                TestPassword,
                second.Code,
                $"SUSPEND {first.Email}");

            var responses = await Task.WhenAll(
                _fixture.Client.SendAsync(firstRequest),
                _fixture.Client.SendAsync(secondRequest));
            try
            {
                var diagnostic = string.Join(
                    Environment.NewLine,
                    await Task.WhenAll(responses.Select(async response =>
                        $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}")));
                Assert.True(
                    responses.Count(response => response.StatusCode == HttpStatusCode.OK) == 1,
                    diagnostic);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Forbidden);
            }
            finally
            {
                foreach (var response in responses) response.Dispose();
            }

            var activeCount = await _fixture.WithDbAsync(db => db.Users.CountAsync(user =>
                (user.Id == first.User.Id || user.Id == second.User.Id) &&
                user.Role == UserRole.Admin &&
                user.Status == ProfileStatus.Active &&
                user.EmailConfirmed &&
                user.TwoFactorEnabled));
            Assert.Equal(1, activeCount);
        }
        finally
        {
            if (otherAdministratorStatuses.Count > 0)
            {
                await _fixture.WithDbAsync(async db =>
                {
                    foreach (var (id, status) in otherAdministratorStatuses)
                    {
                        var administrator = await db.Users.SingleAsync(user => user.Id == id);
                        administrator.Status = status;
                    }
                    await db.SaveChangesAsync();
                    return true;
                });
            }
            await DeleteUserAsync(first.User.Id);
            await DeleteUserAsync(second.User.Id);
        }
    }

    private async Task<(TestUser User, string Email, string Code)> EnableMfaAndRefreshTokenAsync(
        TestUser user)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var stored = await userManager.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(stored);
        Assert.True((await userManager.ResetAuthenticatorKeyAsync(stored)).Succeeded);
        var authenticatorKey = await userManager.GetAuthenticatorKeyAsync(stored);
        Assert.NotNull(authenticatorKey);
        Assert.True((await userManager.SetTwoFactorEnabledAsync(stored, true)).Succeeded);
        var code = GenerateAuthenticatorCode(authenticatorKey);
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>().GenerateToken(stored);
        return (new TestUser(stored.Id, stored.Role, jwt), stored.Email!, code);
    }

    private static string GenerateAuthenticatorCode(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var normalized = base32Key.Trim().TrimEnd('=').ToUpperInvariant();
        var keyBytes = new List<byte>();
        var buffer = 0;
        var bitsInBuffer = 0;
        foreach (var character in normalized)
        {
            var value = alphabet.IndexOf(character, StringComparison.Ordinal);
            Assert.True(value >= 0, "Authenticator key is not valid Base32.");
            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;
            if (bitsInBuffer < 8) continue;
            bitsInBuffer -= 8;
            keyBytes.Add((byte)(buffer >> bitsInBuffer));
            buffer &= (1 << bitsInBuffer) - 1;
        }

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(
            counter,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        using var hmac = new HMACSHA1(keyBytes.ToArray());
        var hash = hmac.ComputeHash(counter.ToArray());
        var offset = hash[^1] & 0x0f;
        var binaryCode = ((hash[offset] & 0x7f) << 24) |
                         ((hash[offset + 1] & 0xff) << 16) |
                         ((hash[offset + 2] & 0xff) << 8) |
                         (hash[offset + 3] & 0xff);
        return (binaryCode % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string Email, string SecurityStamp)> GetMfaStateAsync(Guid userId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        return (user.Email!, user.SecurityStamp!);
    }

    private async Task DeleteUserAsync(Guid userId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is not null)
        {
            var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
            await db.EmailOutboxMessages
                .Where(message => message.Recipient == user.Email)
                .ExecuteDeleteAsync();
            await userManager.DeleteAsync(user);
        }
    }

    private static HttpRequestMessage EmergencyRequest(
        Guid targetId,
        string action,
        TestUser actor,
        string password,
        string authenticatorCode,
        string confirmation)
    {
        var request = Authorized(
            HttpMethod.Post,
            $"/api/admin/management/accounts/{targetId}/{action}",
            actor);
        request.Content = JsonContent.Create(new
        {
            currentPassword = password,
            authenticatorCode,
            confirmation,
            reason = "Confirmed security incident requiring emergency access control."
        });
        return request;
    }

    private static HttpRequestMessage Authorized(
        HttpMethod method,
        string path,
        TestUser actor)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
