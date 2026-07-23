using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class DataAndImageUpdateTests
{
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly ManualTransferTestFixture _fixture;

    public DataAndImageUpdateTests(ManualTransferTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Room_update_replaces_all_images_clears_amenities_and_honours_offline_state()
    {
        var room = await _fixture.CreateRoomAsync();
        using (var addImages = CreateAuthorizedFormRequest(
                   HttpMethod.Post,
                   $"/api/rooms/{room.Id}/images",
                   _fixture.Admin))
        {
            AddPng(addImages.Content!, "files", "old-room.png");
            var added = await _fixture.Client.SendAsync(addImages);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        var oldImage = await _fixture.WithDbAsync(db => db.RoomImages
            .AsNoTracking()
            .SingleAsync(image => image.RoomId == room.Id));
        Assert.True(File.Exists(_fixture.GetLocalAssetPath(oldImage.PublicId)));

        using var update = CreateAuthorizedFormRequest(
            HttpMethod.Put,
            $"/api/rooms/{room.Id}",
            _fixture.Manager);
        AddText(update.Content!, "Name", "Updated Room Name");
        AddText(update.Content!, "IsOnline", "false");
        AddText(update.Content!, "ReplaceAmenities", "true");
        AddText(update.Content!, "ReplaceImages", "true");
        AddPng(update.Content!, "files", "replacement-room.png");

        var response = await _fixture.Client.SendAsync(update);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _fixture.WithDbAsync(db => db.Rooms
            .AsNoTracking()
            .Include(item => item.Images)
            .SingleAsync(item => item.Id == room.Id));
        Assert.Equal("Updated Room Name", stored.Name);
        Assert.False(stored.IsOnline);
        Assert.Empty(stored.Amenities);
        var replacement = Assert.Single(stored.Images);
        Assert.NotEqual(oldImage.PublicId, replacement.PublicId);
        Assert.False(File.Exists(_fixture.GetLocalAssetPath(oldImage.PublicId)));
        Assert.True(File.Exists(_fixture.GetLocalAssetPath(replacement.PublicId)));
    }

    [Fact]
    public async Task Maintenance_room_cannot_be_published_by_an_update()
    {
        var room = await _fixture.CreateRoomAsync();
        using var update = CreateAuthorizedFormRequest(
            HttpMethod.Put,
            $"/api/rooms/{room.Id}",
            _fixture.Admin);
        AddText(update.Content!, "Status", "Maintenance");
        AddText(update.Content!, "IsOnline", "true");

        var response = await _fixture.Client.SendAsync(update);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _fixture.WithDbAsync(db => db.Rooms
            .AsNoTracking()
            .SingleAsync(item => item.Id == room.Id));
        Assert.Equal(RoomStatus.Maintenance, stored.Status);
        Assert.False(stored.IsOnline);
    }

    [Fact]
    public async Task Room_update_rejects_an_image_url_not_owned_by_the_room()
    {
        var room = await _fixture.CreateRoomAsync();
        using var update = CreateAuthorizedFormRequest(
            HttpMethod.Put,
            $"/api/rooms/{room.Id}",
            _fixture.Admin);
        AddText(update.Content!, "ReplaceImages", "true");
        AddText(update.Content!, "Images", "https://example.test/not-this-rooms-image.png");

        var response = await _fixture.Client.SendAsync(update);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already assigned to this room", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Avatar_update_persists_user_and_guest_audits_change_and_cleans_old_asset()
    {
        var guest = await _fixture.LinkGuestProfileAsync(_fixture.Manager);
        var first = await UploadAvatarAsync(_fixture.Manager, "first-avatar.png");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<AvatarUpdateResponse>();
        Assert.NotNull(firstBody);

        var firstState = await _fixture.WithDbAsync(async db => new
        {
            User = await db.Users.AsNoTracking().SingleAsync(user => user.Id == _fixture.Manager.Id),
            Guest = await db.Guests.AsNoTracking().SingleAsync(item => item.Id == guest.Id)
        });
        Assert.Equal(firstBody.Data.AvatarUrl, firstState.User.AvatarUrl);
        Assert.Equal(firstBody.Data.AvatarUrl, firstState.Guest.AvatarUrl);
        Assert.False(string.IsNullOrWhiteSpace(firstState.User.AvatarPublicId));
        Assert.True(File.Exists(_fixture.GetLocalAssetPath(firstState.User.AvatarPublicId!)));

        var second = await UploadAvatarAsync(_fixture.Manager, "second-avatar.png");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<AvatarUpdateResponse>();
        Assert.NotNull(secondBody);

        var secondState = await _fixture.WithDbAsync(async db => new
        {
            User = await db.Users.AsNoTracking().SingleAsync(user => user.Id == _fixture.Manager.Id),
            Guest = await db.Guests.AsNoTracking().SingleAsync(item => item.Id == guest.Id),
            AuditCount = await db.AuditLogs.CountAsync(log =>
                log.ProfileId == _fixture.Manager.Id && log.Action == "PROFILE_AVATAR_UPDATED")
        });
        Assert.Equal(secondBody.Data.AvatarUrl, secondState.User.AvatarUrl);
        Assert.Equal(secondBody.Data.AvatarUrl, secondState.Guest.AvatarUrl);
        Assert.NotEqual(firstState.User.AvatarPublicId, secondState.User.AvatarPublicId);
        Assert.False(File.Exists(_fixture.GetLocalAssetPath(firstState.User.AvatarPublicId!)));
        Assert.True(File.Exists(_fixture.GetLocalAssetPath(secondState.User.AvatarPublicId!)));
        Assert.True(secondState.AuditCount >= 2);
    }

    [Fact]
    public async Task Avatar_database_failure_rolls_back_and_removes_the_new_upload()
    {
        var initial = await UploadAvatarAsync(_fixture.Staff, "stable-avatar.png");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var before = await _fixture.WithDbAsync(db => db.Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == _fixture.Staff.Id));
        Assert.NotNull(before.AvatarPublicId);
        var avatarDirectory = Path.GetDirectoryName(_fixture.GetLocalAssetPath(before.AvatarPublicId!))!;
        var filesBefore = Directory.GetFiles(avatarDirectory).ToHashSet(StringComparer.Ordinal);

        await _fixture.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION fail_avatar_audit() RETURNS trigger AS $$
                BEGIN
                    IF NEW."Action" = 'PROFILE_AVATAR_UPDATED' THEN
                        RAISE EXCEPTION 'forced avatar audit failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_avatar_audit_trigger
                BEFORE INSERT ON audit_logs
                FOR EACH ROW EXECUTE FUNCTION fail_avatar_audit();
                """);
            return true;
        });

        try
        {
            var failed = await UploadAvatarAsync(_fixture.Staff, "must-rollback.png");
            Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);

            var after = await _fixture.WithDbAsync(db => db.Users
                .AsNoTracking()
                .SingleAsync(user => user.Id == _fixture.Staff.Id));
            Assert.Equal(before.AvatarUrl, after.AvatarUrl);
            Assert.Equal(before.AvatarPublicId, after.AvatarPublicId);
            Assert.True(File.Exists(_fixture.GetLocalAssetPath(before.AvatarPublicId!)));
            Assert.Equal(filesBefore, Directory.GetFiles(avatarDirectory).ToHashSet(StringComparer.Ordinal));
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER IF EXISTS fail_avatar_audit_trigger ON audit_logs;
                    DROP FUNCTION IF EXISTS fail_avatar_audit();
                    """);
                return true;
            });
        }
    }

    [Fact]
    public async Task Avatar_update_rejects_an_unauthenticated_request()
    {
        using var form = new MultipartFormDataContent();
        AddPng(form, "file", "anonymous.png");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/profile/me/avatar")
        {
            Content = form
        };
        request.Headers.Add("X-Moore-App-Environment", "local");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Profile_update_keeps_identity_guest_and_audit_in_sync()
    {
        var guest = await _fixture.LinkGuestProfileAsync(_fixture.ClientUser);
        using var request = CreateAuthorizedJsonRequest(
            HttpMethod.Put,
            "/api/profile/me",
            _fixture.ClientUser,
            new
            {
                fullName = "Updated Profile",
                phone = "+2348111111111"
            });

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responsePayload = await response.Content.ReadFromJsonAsync<ProfileUpdateResponse>();
        Assert.NotNull(responsePayload);
        Assert.Equal("+2348111111111", responsePayload.Data.Phone);
        var state = await _fixture.WithDbAsync(async db => new
        {
            User = await db.Users.AsNoTracking().SingleAsync(user => user.Id == _fixture.ClientUser.Id),
            Guest = await db.Guests.AsNoTracking().SingleAsync(item => item.Id == guest.Id),
            AuditCount = await db.AuditLogs.CountAsync(log =>
                log.ProfileId == _fixture.ClientUser.Id && log.Action == "PARTIAL_PROFILE_UPDATE")
        });
        Assert.Equal("Updated Profile", state.User.Name);
        Assert.Equal("+2348111111111", state.User.PhoneNumber);
        Assert.Equal("Updated", state.Guest.FirstName);
        Assert.Equal("Profile", state.Guest.LastName);
        Assert.Equal("+2348111111111", state.Guest.Phone);
        Assert.True(state.AuditCount >= 1);
    }

    private sealed record ProfileUpdateResponse(string Message, UserProfileDto Data);

    [Fact]
    public async Task Staff_update_rolls_back_identity_and_role_when_audit_fails()
    {
        var before = await _fixture.WithDbAsync(async db => new
        {
            User = await db.Users.AsNoTracking().SingleAsync(user => user.Id == _fixture.Staff.Id),
            Roles = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                where userRole.UserId == _fixture.Staff.Id
                orderby role.Name
                select role.Name!).ToListAsync()
        });

        await _fixture.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION fail_staff_update_audit() RETURNS trigger AS $$
                BEGIN
                    IF NEW."Action" = 'STAFF_UPDATED' THEN
                        RAISE EXCEPTION 'forced staff update audit failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_staff_update_audit_trigger
                BEFORE INSERT ON audit_logs
                FOR EACH ROW EXECUTE FUNCTION fail_staff_update_audit();
                """);
            return true;
        });

        try
        {
            using var request = CreateAuthorizedJsonRequest(
                HttpMethod.Put,
                $"/api/admin/management/employees/{_fixture.Staff.Id}",
                _fixture.Admin,
                new
                {
                    fullName = "Must Roll Back",
                    email = before.User.Email,
                    phone = "+2348222222222",
                    assignedRole = "Manager",
                    department = "Reception"
                });
            var response = await _fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var after = await _fixture.WithDbAsync(async db => new
            {
                User = await db.Users.AsNoTracking().SingleAsync(user => user.Id == _fixture.Staff.Id),
                Roles = await (
                    from userRole in db.UserRoles
                    join role in db.Roles on userRole.RoleId equals role.Id
                    where userRole.UserId == _fixture.Staff.Id
                    orderby role.Name
                    select role.Name!).ToListAsync()
            });
            Assert.Equal(before.User.Name, after.User.Name);
            Assert.Equal(before.User.PhoneNumber, after.User.PhoneNumber);
            Assert.Equal(before.User.Role, after.User.Role);
            Assert.Equal(before.User.Department, after.User.Department);
            Assert.Equal(before.Roles, after.Roles);
        }
        finally
        {
            await _fixture.WithDbAsync(async db =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER IF EXISTS fail_staff_update_audit_trigger ON audit_logs;
                    DROP FUNCTION IF EXISTS fail_staff_update_audit();
                    """);
                return true;
            });
        }
    }

    [Fact]
    public async Task Client_registration_atomically_creates_user_guest_and_role_with_retries_enabled()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"new-client-{unique[..12]}@example.test";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/register")
        {
            Content = JsonContent.Create(new
            {
                firstName = "New",
                lastName = "Client",
                email,
                phone = "+2348333333333",
                password = "Registration123!"
            })
        };
        request.Headers.Add("X-Moore-App-Environment", "local");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var state = await _fixture.WithDbAsync(async db =>
        {
            var user = await db.Users.AsNoTracking().SingleAsync(item => item.Email == email);
            var guest = await db.Guests.AsNoTracking().SingleAsync(item => item.Id == user.GuestId);
            var roles = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                where userRole.UserId == user.Id
                select role.Name!).ToListAsync();
            return new { User = user, Guest = guest, Roles = roles };
        });
        Assert.Equal(UserRole.Client, state.User.Role);
        Assert.Equal(ProfileStatus.Active, state.User.Status);
        Assert.Equal(email, state.Guest.Email);
        Assert.Equal(19, state.Guest.Id.Length);
        Assert.Contains("Client", state.Roles);
    }

    [Fact]
    public async Task Password_contract_accepts_eight_and_rejects_seven_characters()
    {
        var passwordParameter = typeof(RegisterRequest)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(parameter =>
                parameter.Name == nameof(RegisterRequest.Password));
        var lengthRule = passwordParameter
            .GetCustomAttribute<StringLengthAttribute>();
        Assert.NotNull(lengthRule);
        Assert.Equal(8, lengthRule.MinimumLength);
        Assert.True(lengthRule.IsValid("Aa1!bcde"));
        Assert.False(lengthRule.IsValid("Aa1!bcd"));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;
        Assert.Equal(8, options.Password.RequiredLength);

        var userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var candidate = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "password-validator@example.test",
            UserName = "password-validator@example.test",
            Name = "Password Validator"
        };
        var accepted = await Task.WhenAll(userManager.PasswordValidators.Select(
            validator => validator.ValidateAsync(
                userManager,
                candidate,
                "Aa1!bcde")));
        var rejected = await Task.WhenAll(userManager.PasswordValidators.Select(
            validator => validator.ValidateAsync(
                userManager,
                candidate,
                "Aa1!bcd")));

        Assert.All(accepted, result => Assert.True(result.Succeeded));
        Assert.Contains(rejected, result => !result.Succeeded);
    }

    private Task<HttpResponseMessage> UploadAvatarAsync(TestUser actor, string fileName)
    {
        var request = CreateAuthorizedFormRequest(HttpMethod.Put, "/api/profile/me/avatar", actor);
        AddPng(request.Content!, "file", fileName);
        return SendAndDisposeAsync(request);
    }

    private async Task<HttpResponseMessage> SendAndDisposeAsync(HttpRequestMessage request)
    {
        using (request)
        {
            return await _fixture.Client.SendAsync(request);
        }
    }

    private static HttpRequestMessage CreateAuthorizedFormRequest(
        HttpMethod method,
        string path,
        TestUser actor)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new MultipartFormDataContent()
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private static HttpRequestMessage CreateAuthorizedJsonRequest(
        HttpMethod method,
        string path,
        TestUser actor,
        object body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private static void AddPng(HttpContent formContent, string fieldName, string fileName)
    {
        var form = Assert.IsType<MultipartFormDataContent>(formContent);
        var image = new ByteArrayContent(PngBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, fieldName, fileName);
    }

    private static void AddText(HttpContent formContent, string fieldName, string value)
    {
        var form = Assert.IsType<MultipartFormDataContent>(formContent);
        form.Add(new StringContent(value), fieldName);
    }
}
