using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.WebAPI.Controllers;

namespace MooreHotels.IntegrationTests;

public sealed class ControllerSecurityMetadataTests
{
    [Fact]
    public void Every_controller_action_declares_its_access_boundary()
    {
        var controllerTypes = typeof(AuthController).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .OrderBy(type => type.FullName)
            .ToArray();

        Assert.NotEmpty(controllerTypes);
        foreach (var controllerType in controllerTypes)
        {
            var controllerAuthorized = controllerType.IsDefined(
                typeof(AuthorizeAttribute),
                inherit: true);
            var actions = controllerType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(true).Any())
                .ToArray();

            Assert.NotEmpty(actions);
            foreach (var action in actions)
            {
                var allowsAnonymous = action.IsDefined(typeof(AllowAnonymousAttribute), true);
                var actionAuthorized = action.IsDefined(typeof(AuthorizeAttribute), true);
                Assert.True(
                    allowsAnonymous || actionAuthorized || controllerAuthorized,
                    $"{controllerType.Name}.{action.Name} has no explicit access-control metadata.");

                if (allowsAnonymous &&
                    !(controllerType == typeof(HealthController) &&
                      action.Name is nameof(HealthController.Ready) or nameof(HealthController.Operations)))
                {
                    Assert.True(
                        action.IsDefined(typeof(EnableRateLimitingAttribute), true),
                        $"Anonymous endpoint {controllerType.Name}.{action.Name} has no rate-limit policy.");
                }
            }
        }
    }

    [Theory]
    [InlineData(typeof(MfaController), nameof(MfaController.Setup))]
    [InlineData(typeof(MfaController), nameof(MfaController.Enable))]
    [InlineData(typeof(ProfileController), nameof(ProfileController.RotateCredentials))]
    public void Credential_mutations_are_explicitly_rate_limited(Type controller, string action)
    {
        var method = controller.GetMethod(action);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(EnableRateLimitingAttribute), true));
    }

    [Fact]
    public void Guest_booking_contract_omits_physical_room_assignment_details()
    {
        Assert.Null(typeof(PublicBookingDto).GetProperty("RoomId"));
        Assert.Null(typeof(PublicReservationRoomDto).GetProperty("AssignedRoomId"));
        Assert.Null(typeof(PublicReservationRoomDto).GetProperty("AssignedRoomNumber"));
        Assert.Null(typeof(PublicReservationRoomDto).GetProperty("AssignedAtUtc"));
    }

    [Fact]
    public void Public_inventory_contract_omits_internal_stock_and_occupancy_counts()
    {
        Assert.Null(typeof(PublicRoomTypeDto).GetProperty("PhysicalRoomCount"));
        Assert.Null(typeof(PublicRoomTypeDto).GetProperty("UpdatedAtUtc"));
        Assert.Null(typeof(PublicRoomTypeAvailabilityDto).GetProperty("AvailableUnits"));
        Assert.Null(typeof(PublicRoomTypeAvailabilityDto).GetProperty("Days"));
    }

    [Fact]
    public void Public_add_on_contract_omits_publication_state_and_internal_timestamps()
    {
        Assert.Null(typeof(PublicAddOnServiceDto).GetProperty("IsActive"));
        Assert.Null(typeof(PublicAddOnServiceDto).GetProperty("CreatedAt"));
    }
}

[Collection(ManualTransferCollection.Name)]
public sealed class ControllerDataExposureTests
{
    private readonly ManualTransferTestFixture _fixture;

    public ControllerDataExposureTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Anonymous_add_on_catalog_never_exposes_inactive_services()
    {
        var inactive = await _fixture.WithDbAsync(async db =>
        {
            var item = new AddOnService
            {
                Id = Guid.NewGuid(),
                Name = $"Hidden service {Guid.NewGuid():N}",
                Description = "Unpublished catalog item.",
                Category = AddOnCategory.Other,
                Price = 1000m,
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            };
            db.AddOnServices.Add(item);
            await db.SaveChangesAsync();
            return item;
        });

        using (var list = await _fixture.Client.GetAsync("/api/addons?onlyActive=false"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.DoesNotContain(inactive.Id.ToString(), await list.Content.ReadAsStringAsync());
        }

        using (var item = await _fixture.Client.GetAsync($"/api/addons/{inactive.Id}"))
            Assert.Equal(HttpStatusCode.NotFound, item.StatusCode);

        using var managedRequest = Authorized(
            HttpMethod.Get,
            $"/api/addons/management/{inactive.Id}",
            _fixture.Manager);
        using var managed = await _fixture.Client.SendAsync(managedRequest);
        Assert.Equal(HttpStatusCode.OK, managed.StatusCode);
    }

    [Fact]
    public async Task Public_room_routes_never_switch_to_internal_shape_for_authenticated_users()
    {
        var room = await _fixture.CreateRoomAsync();
        using var request = Authorized(HttpMethod.Get, $"/api/rooms/{room.Id}", _fixture.Admin);
        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("roomNumber", out _));
        Assert.False(body.RootElement.TryGetProperty("floor", out _));
        Assert.False(body.RootElement.TryGetProperty("status", out _));
        Assert.False(body.RootElement.TryGetProperty("isOnline", out _));
        Assert.False(body.RootElement.TryGetProperty("createdAt", out _));

        using var managedRequest = Authorized(
            HttpMethod.Get,
            $"/api/rooms/management/{room.Id}",
            _fixture.Admin);
        using var managed = await _fixture.Client.SendAsync(managedRequest);
        Assert.Equal(HttpStatusCode.OK, managed.StatusCode);
        using var managedBody = JsonDocument.Parse(await managed.Content.ReadAsStringAsync());
        Assert.Equal(room.RoomNumber, managedBody.RootElement.GetProperty("roomNumber").GetString());
    }

    [Fact]
    public async Task Offline_room_availability_is_not_discoverable_anonymously()
    {
        var room = await _fixture.CreateRoomAsync();
        await _fixture.WithDbAsync(async db =>
        {
            await db.Rooms
                .Where(item => item.Id == room.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.IsOnline, false)
                    .SetProperty(item => item.Status, RoomStatus.Maintenance));
            return true;
        });

        var checkIn = DateTime.UtcNow.AddDays(2).ToString("O");
        var checkOut = DateTime.UtcNow.AddDays(3).ToString("O");
        using var response = await _fixture.Client.GetAsync(
            $"/api/rooms/{room.Id}/availability?checkIn={Uri.EscapeDataString(checkIn)}&checkOut={Uri.EscapeDataString(checkOut)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_does_not_advertise_client_supplied_payment_references_for_verification()
    {
        using var response = await _fixture.Client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var swagger = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = swagger.RootElement
            .GetProperty("paths")
            .GetProperty("/api/bookings/{code}/verify-monnify")
            .GetProperty("post");
        if (!operation.TryGetProperty("parameters", out var parameters)) return;

        var names = parameters.EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString())
            .ToArray();
        Assert.DoesNotContain("paymentReference", names);
        Assert.DoesNotContain("transactionReference", names);
    }

    [Fact]
    public async Task Managers_cannot_enumerate_administrator_profiles()
    {
        using var managerRequest = Authorized(
            HttpMethod.Get,
            "/api/admin/management/employees",
            _fixture.Manager);
        using var managerResponse = await _fixture.Client.SendAsync(managerRequest);
        Assert.Equal(HttpStatusCode.OK, managerResponse.StatusCode);
        var managerPayload = await managerResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_fixture.Admin.Id.ToString(), managerPayload);

        using var adminRequest = Authorized(
            HttpMethod.Get,
            "/api/admin/management/employees",
            _fixture.Admin);
        using var adminResponse = await _fixture.Client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        Assert.Contains(_fixture.Admin.Id.ToString(), await adminResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mfa_secret_requires_current_password_and_is_available_to_clients()
    {
        var client = await _fixture.CreateUserAsync(UserRole.Client);
        using (var wrongPassword = Authorized(
                   HttpMethod.Post,
                   "/api/mfa/setup",
                   client))
        {
            wrongPassword.Content = JsonContent.Create(new
            {
                currentPassword = "DefinitelyNotThePassword123!"
            });
            using var rejected = await _fixture.Client.SendAsync(wrongPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.DoesNotContain("sharedKey", await rejected.Content.ReadAsStringAsync());
        }

        using var validPassword = Authorized(
            HttpMethod.Post,
            "/api/mfa/setup",
            client);
        validPassword.Content = JsonContent.Create(new
        {
            currentPassword = "TransferTest123!"
        });
        using var accepted = await _fixture.Client.SendAsync(validPassword);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("sharedKey").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("accessToken").GetString()));
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, TestUser actor)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }
}
