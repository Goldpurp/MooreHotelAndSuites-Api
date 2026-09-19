using MooreHotels.Infrastructure.Services;

namespace MooreHotels.IntegrationTests;

public sealed class CloudinaryUploadContractTests
{
    [Fact]
    public void Incoming_upload_uses_only_upload_safe_transformations()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var parameters = CloudinaryService.BuildUploadParameters("room.jpg", stream, "rooms");
        var transformation = parameters.Transformation.ToString();

        // Cloudinary explicitly prohibits browser-dependent f_auto on incoming uploads.
        Assert.DoesNotContain("f_auto", transformation, StringComparison.Ordinal);
        Assert.Contains("c_limit", transformation, StringComparison.Ordinal);
        Assert.Contains("w_1200", transformation, StringComparison.Ordinal);
        Assert.Contains("h_800", transformation, StringComparison.Ordinal);
        Assert.Contains("q_auto", transformation, StringComparison.Ordinal);
        Assert.Equal("MooreHotels/rooms", parameters.Folder);
        Assert.True(parameters.EagerAsync);
    }
}

public sealed class UploadErrorResponseTests
{
    [Xunit.Theory]
    [Xunit.InlineData("image_upload_unavailable", true)]
    [Xunit.InlineData("untrusted-provider-text", false)]
    public async Task Production_response_exposes_only_the_allowlisted_error_code(string code, bool expected)
    {
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment
        {
            EnvironmentName = "Production"
        };
        var middleware = new MooreHotels.WebAPI.Middleware.ExceptionHandlingMiddleware(
            _ => throw new MooreHotels.Application.Exceptions.ServiceUnavailableException("private credential details") { ErrorCode = code },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MooreHotels.WebAPI.Middleware.ExceptionHandlingMiddleware>.Instance,
            environment);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        var response = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(503, context.Response.StatusCode);
        Assert.DoesNotContain("private credential details", response, StringComparison.Ordinal);
        Assert.Equal(expected, response.Contains("image_upload_unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain("untrusted-provider-text", response, StringComparison.Ordinal);
    }
}
