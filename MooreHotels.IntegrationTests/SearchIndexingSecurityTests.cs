using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MooreHotels.WebAPI.Middleware;

namespace MooreHotels.IntegrationTests;

public sealed class SearchIndexingSecurityTests
{
    [Fact]
    public async Task Api_responses_are_marked_noindex()
    {
        var environment = new TestHostEnvironment
        {
            EnvironmentName = Environments.Production,
            ApplicationName = "MooreHotels.IntegrationTests",
            ContentRootPath = Directory.GetCurrentDirectory(),
            ContentRootFileProvider = new NullFileProvider()
        };
        var middleware = new SecurityHeadersMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            environment);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(
            "noindex, nofollow, noarchive",
            context.Response.Headers["X-Robots-Tag"].ToString());
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
