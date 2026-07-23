using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MooreHotels.WebAPI.Configuration;

namespace MooreHotels.IntegrationTests;

public sealed class ProductionConfigurationSecurityTests
{
    [Fact]
    public void Production_accepts_only_the_hardened_monnify_boundary()
    {
        var configuration = BuildConfiguration(BaselineSettings());

        ConfigurationBootstrap.ValidateForStartup(
            configuration,
            ProductionEnvironment());
    }

    [Fact]
    public void Production_accepts_disabled_monnify_without_provider_credentials()
    {
        var settings = BaselineSettings();
        settings["MonnifySettings:Enabled"] = "false";
        settings.Remove("MonnifySettings:ApiKey");
        settings.Remove("MonnifySettings:SecretKey");
        settings.Remove("MonnifySettings:ContractCode");

        ConfigurationBootstrap.ValidateForStartup(
            BuildConfiguration(settings),
            ProductionEnvironment());
    }

    [Fact]
    public void Production_accepts_the_single_hop_render_edge_boundary()
    {
        var settings = BaselineSettings();
        settings.Remove("ForwardedHeaders:KnownNetworks:0");
        settings["ForwardedHeaders:TrustRenderEdge"] = "true";

        ConfigurationBootstrap.ValidateForStartup(
            BuildConfiguration(settings),
            ProductionEnvironment());
    }

    [Fact]
    public void Production_rejects_render_edge_mode_with_more_than_one_hop()
    {
        var settings = BaselineSettings();
        settings.Remove("ForwardedHeaders:KnownNetworks:0");
        settings["ForwardedHeaders:TrustRenderEdge"] = "true";
        settings["ForwardedHeaders:ForwardLimit"] = "2";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));

        Assert.Contains("exactly 1", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "MonnifySettings:BaseUrl",
        "https://api.monnify.com.attacker.example")]
    [InlineData(
        "MonnifySettings:AllowedWebhookIpAddresses:0",
        "203.0.113.10")]
    [InlineData(
        "MonnifySettings:EnforceWebhookIpAllowlist",
        "false")]
    [InlineData(
        "ForwardedHeaders:KnownNetworks:0",
        "0.0.0.0 / 0")]
    public void Production_rejects_security_boundary_weakening(
        string key,
        string value)
    {
        var settings = BaselineSettings();
        settings[key] = value;

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));
    }

    [Fact]
    public void Production_rejects_example_placeholders_and_disabled_integrations()
    {
        var settings = BaselineSettings();
        settings["MonnifySettings:SecretKey"] = "<PROD_MONNIFY_SECRET>";
        settings["Runtime:EnableExternalServices"] = "false";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));

        Assert.Contains(
            "MonnifySettings:SecretKey",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Runtime:EnableExternalServices",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(
        IDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static Dictionary<string, string?> BaselineSettings() => new()
    {
        ["ConnectionStrings:DefaultConnection"] =
            "Host=db.example.test;Database=moore;Username=moore_app;Password=strong-db-secret;SSL Mode=VerifyFull;Root Certificate=/tmp/ca.crt",
        ["Jwt:Key"] = "0123456789abcdef0123456789abcdef",
        ["Jwt:Issuer"] = "MooreHotels",
        ["Jwt:Audience"] = "MooreHotelsClients",
        ["Jwt:ExpirationMinutes"] = "60",
        ["AllowedOrigins:0"] = "https://moorehotelandsuites.com",
        ["AllowedHosts"] = "api.moorehotelandsuites.com",
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:ForwardLimit"] = "1",
        ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
        ["Runtime:EnableSwagger"] = "false",
        ["Runtime:EnableExternalServices"] = "true",
        ["Runtime:AutoConfirmEmail"] = "false",
        ["DataProtection:KeysPath"] = "/var/data/moorehotels-keys",
        ["DataProtection:CertificatePath"] =
            "/etc/secrets/moorehotels-data-protection.pfx",
        ["DataProtection:CertificatePassword"] = "certificate-secret",
        ["CloudinarySettings:CloudName"] = "moore-hotels",
        ["CloudinarySettings:ApiKey"] = "cloudinary-key",
        ["CloudinarySettings:ApiSecret"] = "cloudinary-secret",
        ["EmailSettings:ApiPass"] = "email-api-secret",
        ["EmailSettings:SenderEmail"] = "noreply@example.test",
        ["EmailSettings:AdminNotificationEmail"] = "admin@example.test",
        ["MonnifySettings:ApiKey"] = "monnify-live-api-key-test",
        ["MonnifySettings:Enabled"] = "true",
        ["MonnifySettings:SecretKey"] = "monnify-live-secret-test",
        ["MonnifySettings:ContractCode"] = "1234567890",
        ["MonnifySettings:BaseUrl"] = "https://api.monnify.com",
        ["MonnifySettings:EnforceWebhookIpAllowlist"] = "true",
        ["MonnifySettings:AllowedWebhookIpAddresses:0"] =
            "35.242.133.146",
        ["BankTransferSettings:BankName"] = "Example Bank",
        ["BankTransferSettings:AccountName"] = "Moore Hotels",
        ["BankTransferSettings:AccountNumber"] = "0000000000",
        ["PublicAppUrl"] = "https://moorehotelandsuites.com",
        ["DashboardUrl"] = "https://admin.moorehotelandsuites.com",
        ["Api:PublicBaseUrl"] = "https://api.moorehotelandsuites.com",
        ["SeedAdmin"] = "false"
    };

    private static IHostEnvironment ProductionEnvironment() =>
        new TestHostEnvironment
        {
            EnvironmentName = Environments.Production,
            ApplicationName = "MooreHotels.IntegrationTests",
            ContentRootPath = Directory.GetCurrentDirectory(),
            ContentRootFileProvider =
                new NullFileProvider()
        };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
