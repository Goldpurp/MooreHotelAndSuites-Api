using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MooreHotels.WebAPI.Extensions;

namespace MooreHotels.IntegrationTests;

public sealed class ForwardedHeadersConfigurationTests
{
    [Fact]
    public void Render_edge_processes_the_trusted_final_hop_without_requiring_header_symmetry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=localhost;Database=moore;Username=postgres;Password=test-password",
                ["ForwardedHeaders:Enabled"] = "true",
                ["ForwardedHeaders:TrustRenderEdge"] = "true",
                ["ForwardedHeaders:ForwardLimit"] = "1",
                ["Jwt:Key"] = "0123456789abcdef0123456789abcdef",
                ["Jwt:Issuer"] = "MooreHotels",
                ["Jwt:Audience"] = "MooreHotelsClients",
                ["AllowedOrigins:0"] = "https://moorehotelandsuites.com",
                ["Runtime:EnableExternalServices"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMooreHotelsApi(configuration, new TestHostEnvironment());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.False(options.RequireHeaderSymmetry);
        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownNetworks);
    }

    [Fact]
    public void Explicit_proxy_boundaries_keep_strict_header_symmetry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=localhost;Database=moore;Username=postgres;Password=test-password",
                ["ForwardedHeaders:Enabled"] = "true",
                ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
                ["ForwardedHeaders:ForwardLimit"] = "1",
                ["Jwt:Key"] = "0123456789abcdef0123456789abcdef",
                ["Jwt:Issuer"] = "MooreHotels",
                ["Jwt:Audience"] = "MooreHotelsClients",
                ["AllowedOrigins:0"] = "https://moorehotelandsuites.com",
                ["Runtime:EnableExternalServices"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMooreHotelsApi(configuration, new TestHostEnvironment());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.True(options.RequireHeaderSymmetry);
        Assert.Single(options.KnownNetworks);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Local";
        public string ApplicationName { get; set; } = "MooreHotels.IntegrationTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
