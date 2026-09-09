using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Enums;
using MooreHotels.WebAPI.Controllers;

namespace MooreHotels.IntegrationTests;

public sealed class ControllerIdentityGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("invalid-user-id")]
    public async Task Image_upload_requires_valid_actor_before_inspecting_request(string? actorId)
    {
        var controller = new ImagesController(
            null!, null!, null!, null!, NullLogger<ImagesController>.Instance)
        {
            ControllerContext = Context(actorId)
        };

        var result = await controller.Upload(null!);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-user-id")]
    public async Task Privacy_update_requires_valid_actor_before_inspecting_request(string? actorId)
    {
        var controller = new PrivacyController(
            null!, Options.Create(new PrivacySettings()), null!)
        {
            ControllerContext = Context(actorId)
        };

        var result = await controller.UpdateRequest(
            Guid.NewGuid(),
            new UpdatePrivacyRequestRequest(DataSubjectRequestStatus.Pending, null),
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    private static ControllerContext Context(string? actorId)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin") };
        if (actorId is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, actorId));
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }
}
