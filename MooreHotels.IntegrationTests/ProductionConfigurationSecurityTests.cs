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
    public void Production_rejects_the_removed_render_private_database_override()
    {
        var settings = BaselineSettings();
        settings["ConnectionStrings:DefaultConnection"] =
            "Host=dpg-example123-a;Database=moore;Username=moore_app;Password=strong-db-secret;SSL Mode=Disable";
        settings["Database:TrustRenderPrivateNetwork"] = "true";

        Assert.Throws<InvalidOperationException>(() => ConfigurationBootstrap.ValidateForStartup(
            BuildConfiguration(settings),
            ProductionEnvironment()));
    }

    [Fact]
    public void Production_accepts_the_supabase_ipv4_session_pooler_with_full_tls_verification()
    {
        var settings = BaselineSettings();
        settings["Database:Provider"] = "Supabase";
        settings["ConnectionStrings:DefaultConnection"] =
            "Host=aws-0-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=moore_runtime.projectref;Password=strong-db-secret;Maximum Pool Size=20;SSL Mode=VerifyFull";

        ConfigurationBootstrap.ValidateForStartup(
            BuildConfiguration(settings),
            ProductionEnvironment());
    }

    [Fact]
    public void Local_accepts_only_an_isolated_loopback_profile()
    {
        ConfigurationBootstrap.ValidateForStartup(
            BuildConfiguration(LocalSettings()),
            LocalEnvironment());
    }

    [Fact]
    public void Local_accepts_an_explicit_single_label_test_container_database()
    {
        var settings = LocalSettings();
        settings["ConnectionStrings:DefaultConnection"] =
            "Host=moore-test-db;Database=moore_hotels_tests;Username=postgres;Password=test-secret;SSL Mode=Disable";
        settings["Database:AllowLocalContainerHost"] = "true";

        ConfigurationBootstrap.ValidateForStartup(
            BuildConfiguration(settings),
            LocalEnvironment());
    }

    [Theory]
    [InlineData("Database:Provider", "Supabase")]
    [InlineData("ConnectionStrings:DefaultConnection", "Host=aws-0-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres;Password=secret;SSL Mode=VerifyFull")]
    [InlineData("ConnectionStrings:DefaultConnection", "Host=db.example.com;Database=moore_hotels_local;Username=postgres;Password=secret;SSL Mode=VerifyFull")]
    [InlineData("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Database=postgres;Username=postgres;Password=secret;SSL Mode=Disable")]
    [InlineData("Runtime:EnableExternalServices", "true")]
    [InlineData("EmailSettings:DeliveryMode", "Brevo")]
    [InlineData("PublicAppUrl", "https://moorehotelandsuites.com")]
    [InlineData("DashboardUrl", "https://admin.moorehotelandsuites.com")]
    [InlineData("Api:PublicBaseUrl", "https://api.moorehotelandsuites.com")]
    [InlineData("AllowedOrigins:0", "https://moorehotelandsuites.com")]
    [InlineData("AllowedHosts", "api.moorehotelandsuites.com")]
    [InlineData("Jwt:Issuer", "MooreHotels")]
    [InlineData("Jwt:Audience", "MooreHotels_Clients")]
    [InlineData("Database:ApplyMigrationsOnStartup", "false")]
    [InlineData("ForwardedHeaders:Enabled", "true")]
    [InlineData("ForwardedHeaders:TrustRenderEdge", "true")]
    [InlineData("ForwardedHeaders:KnownProxies:0", "10.0.0.1")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8")]
    public void Local_rejects_a_cross_environment_setting(string key, string value)
    {
        var settings = LocalSettings();
        settings[key] = value;

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                LocalEnvironment()));
    }

    [Fact]
    public void Local_provider_test_doubles_do_not_enable_external_network_calls()
    {
        var settings = LocalSettings();
        settings["MonnifySettings:Enabled"] = "true";
        settings["Runtime:AllowLocalProviderTestDoubles"] = "true";

        ConfigurationBootstrap.ValidateForStartup(
            BuildConfiguration(settings),
            LocalEnvironment());
    }

    [Theory]
    [InlineData("Local", "Production")]
    [InlineData("Production", "Local")]
    public void Conflicting_process_environment_names_fail_closed(
        string aspNetEnvironment,
        string dotNetEnvironment)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AppEnvironments.Resolve(aspNetEnvironment, dotNetEnvironment));
    }

    [Theory]
    [InlineData("db.projectref.supabase.co", 5432, "VerifyFull")]
    [InlineData("aws-0-eu-central-1.pooler.supabase.com", 6543, "VerifyFull")]
    [InlineData("aws-0-eu-central-1.pooler.supabase.com", 5432, "Require")]
    [InlineData("database.example.test", 5432, "VerifyFull")]
    public void Production_rejects_an_unsafe_or_incompatible_supabase_runtime_connection(
        string host,
        int port,
        string sslMode)
    {
        var settings = BaselineSettings();
        settings["Database:Provider"] = "Supabase";
        settings["ConnectionStrings:DefaultConnection"] =
            $"Host={host};Port={port};Database=postgres;Username=moore_runtime.projectref;Password=strong-db-secret;Maximum Pool Size=20;SSL Mode={sslMode}";

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));
    }

    [Theory]
    [InlineData("Maximum Pool Size=100")]
    [InlineData("Pooling=false")]
    [InlineData("Maximum Pool Size=20;Multiplexing=true")]
    public void Production_rejects_an_unsafe_supabase_client_pool(string poolSettings)
    {
        var settings = BaselineSettings();
        settings["Database:Provider"] = "Supabase";
        settings["ConnectionStrings:DefaultConnection"] =
            $"Host=aws-0-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=moore_runtime.projectref;Password=strong-db-secret;{poolSettings};SSL Mode=VerifyFull";

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));
    }

    [Theory]
    [InlineData("db.example.test", "true")]
    [InlineData("dpg-example123-a", "false")]
    public void Production_rejects_non_tls_database_outside_the_explicit_render_boundary(
        string host,
        string trustRenderPrivateNetwork)
    {
        var settings = BaselineSettings();
        settings["ConnectionStrings:DefaultConnection"] =
            $"Host={host};Database=moore;Username=moore_app;Password=strong-db-secret;SSL Mode=Disable";
        settings["Database:TrustRenderPrivateNetwork"] = trustRenderPrivateNetwork;

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));
    }

    [Fact]
    public void Free_hosting_accepts_database_keys_and_honest_logical_backup_declarations()
    {
        var settings = BaselineSettings();
        settings["OperationalReadiness:BackupMode"] = "Logical";
        settings["OperationalReadiness:ManagedBackupsEnabled"] = "false";
        settings["OperationalReadiness:PointInTimeRecoveryEnabled"] = "false";
        settings["DataProtection:StorageProvider"] = "Database";
        settings.Remove("DataProtection:KeysPath");
        ConfigurationBootstrap.ValidateForStartup(BuildConfiguration(settings), ProductionEnvironment());
        settings["OperationalReadiness:EncryptedOffProviderBackupsEnabled"] = "false";
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(BuildConfiguration(settings), ProductionEnvironment()));
    }

    [Theory]
    [InlineData("OperationalReadiness:BackupMode", "Unknown")]
    [InlineData("DataProtection:StorageProvider", "Memory")]
    public void Production_rejects_unknown_storage_and_backup_modes(string key, string value)
    {
        var settings = BaselineSettings();
        settings[key] = value;
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(BuildConfiguration(settings), ProductionEnvironment()));
    }

    [Fact]
    public void Production_accepts_a_base64_data_protection_certificate()
    {
        var settings = BaselineSettings();
        settings.Remove("DataProtection:CertificatePath");
        settings["DataProtection:CertificateBase64"] =
            Convert.ToBase64String("test-certificate-bytes"u8);

        ConfigurationBootstrap.ValidateForStartup(
            BuildConfiguration(settings),
            ProductionEnvironment());
    }

    [Fact]
    public void Production_rejects_an_invalid_base64_data_protection_certificate()
    {
        var settings = BaselineSettings();
        settings.Remove("DataProtection:CertificatePath");
        settings["DataProtection:CertificateBase64"] = "not-valid-base64";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));

        Assert.Contains(
            "DataProtection:CertificateBase64",
            exception.Message,
            StringComparison.Ordinal);
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
    [InlineData(
        "Runtime:EnableRateLimiting",
        "false")]
    [InlineData(
        "Runtime:RequirePublicBookingEmailVerification",
        "false")]
    [InlineData(
        "Runtime:EnableMediaDeletion",
        "false")]
    [InlineData(
        "Runtime:EnableBookingExpiration",
        "false")]
    [InlineData(
        "Runtime:ExternalRequestTimeoutSeconds",
        "120")]
    [InlineData(
        "Database:ApplyMigrationsOnStartup",
        "true")]
    [InlineData(
        "Database:CreateIfMissing",
        "true")]
    [InlineData(
        "Database:ContextPoolSize",
        "4")]
    [InlineData(
        "Database:Provider",
        "UnknownProvider")]
    [InlineData(
        "Security:RequireStaffMfa",
        "false")]
    [InlineData(
        "Privacy:RequirePolicyAcceptance",
        "false")]
    [InlineData(
        "Privacy:EnableRetentionWorker",
        "false")]
    [InlineData(
        "Privacy:InactiveAccountRetentionDays",
        "30")]
    [InlineData(
        "Pricing:RequireQuoteForBooking",
        "false")]
    [InlineData(
        "OperationalReadiness:ManagedBackupsEnabled",
        "false")]
    [InlineData(
        "OperationalReadiness:PointInTimeRecoveryEnabled",
        "false")]
    [InlineData(
        "OperationalReadiness:QueueAgeAlertsEnabled",
        "false")]
    [InlineData(
        "ProviderAcceptance:Brevo:EvidenceReference",
        "")]
    [InlineData(
        "ProviderAcceptance:Cloudinary:EvidenceReference",
        "")]
    [InlineData(
        "ProviderAcceptance:HostedPaymentPageOnly",
        "false")]
    [InlineData(
        "Runtime:AllowLocalProviderTestDoubles",
        "true")]
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

    [Theory]
    [InlineData("Include Error Detail=true")]
    [InlineData("Persist Security Info=true")]
    [InlineData("No Reset On Close=true")]
    [InlineData("Search Path=attacker_schema")]
    public void Production_rejects_database_options_that_can_leak_or_cross_pool_state(
        string unsafeOption)
    {
        var settings = BaselineSettings();
        settings["ConnectionStrings:DefaultConnection"] =
            $"Host=db.example.test;Database=moore;Username=moore_app;Password=strong-db-secret;SSL Mode=VerifyFull;{unsafeOption}";

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));
    }

    [Theory]
    [InlineData("AllowedOrigins:0", "https://moorehotelandsuites.com/?debug=true")]
    [InlineData("AllowedHosts", "https://api.moorehotelandsuites.com")]
    [InlineData("AllowedHosts", "other.example.com")]
    [InlineData("Api:PublicBaseUrl", "https://api.moorehotelandsuites.com/path")]
    public void Production_rejects_non_origin_or_host_filter_configuration(
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
    public void Production_rejects_stale_restore_drill_evidence()
    {
        var settings = BaselineSettings();
        settings["OperationalReadiness:RestoreDrillMaximumAgeDays"] = "90";
        settings["OperationalReadiness:LastRestoreDrillAtUtc"] =
            DateTimeOffset.UtcNow.AddDays(-91).ToString("O");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBootstrap.ValidateForStartup(
                BuildConfiguration(settings),
                ProductionEnvironment()));

        Assert.Contains("older than", exception.Message, StringComparison.Ordinal);
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
        ["Jwt:Audience"] = "MooreHotels_Clients",
        ["Jwt:ExpirationMinutes"] = "60",
        ["AllowedOrigins:0"] = "https://moorehotelandsuites.com",
        ["AllowedHosts"] = "api.moorehotelandsuites.com",
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:ForwardLimit"] = "1",
        ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
        ["Runtime:EnableSwagger"] = "false",
        ["Runtime:EnableExternalServices"] = "true",
        ["Runtime:EnableRateLimiting"] = "true",
        ["Runtime:RequirePublicBookingEmailVerification"] = "true",
        ["Runtime:EnableMediaDeletion"] = "true",
        ["Runtime:AutoConfirmEmail"] = "false",
        ["Security:RequireStaffMfa"] = "true",
        ["Privacy:RequirePolicyAcceptance"] = "true",
        ["Privacy:CurrentPrivacyPolicyVersion"] = "2026-09-04",
        ["Privacy:CurrentBookingTermsVersion"] = "2026-09-04",
        ["Privacy:PrivacyPolicyUrl"] = "https://moorehotelandsuites.com/privacy",
        ["Privacy:BookingTermsUrl"] = "https://moorehotelandsuites.com/terms",
        ["Privacy:EnableRetentionWorker"] = "true",
        ["Privacy:GuestRetentionDays"] = "2555",
        ["Privacy:InactiveAccountRetentionDays"] = "2555",
        ["Pricing:DefaultCurrency"] = "NGN",
        ["Pricing:QuoteLifetimeMinutes"] = "15",
        ["Pricing:RequireQuoteForBooking"] = "true",
        ["OperationalReadiness:ManagedBackupsEnabled"] = "true",
        ["OperationalReadiness:PointInTimeRecoveryEnabled"] = "true",
        ["OperationalReadiness:EncryptedOffProviderBackupsEnabled"] = "true",
        ["OperationalReadiness:RecoveryPointObjectiveMinutes"] = "60",
        ["OperationalReadiness:RecoveryTimeObjectiveMinutes"] = "240",
        ["OperationalReadiness:RestoreDrillMaximumAgeDays"] = "100",
        ["OperationalReadiness:LastRestoreDrillAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
        ["OperationalReadiness:RestoreDrillEvidenceReference"] = "DRILL-TEST-001",
        ["OperationalReadiness:UptimeAlertsEnabled"] = "true",
        ["OperationalReadiness:ApiErrorAndLatencyAlertsEnabled"] = "true",
        ["OperationalReadiness:QueueAgeAlertsEnabled"] = "true",
        ["OperationalReadiness:PaymentAndWebhookAlertsEnabled"] = "true",
        ["OperationalReadiness:AlertRoutingEvidenceReference"] = "ALERT-TEST-001",
        ["OperationalReadiness:QueueAgeWarningMinutes"] = "15",
        ["OperationalReadiness:PaymentPendingWarningMinutes"] = "30",
        ["ProviderAcceptance:Brevo:CredentialRotationReference"] = "ROTATE-BREVO-001",
        ["ProviderAcceptance:Brevo:AcceptedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
        ["ProviderAcceptance:Brevo:EvidenceReference"] = "BREVO-TEST-001",
        ["ProviderAcceptance:Cloudinary:CredentialRotationReference"] = "ROTATE-CLOUDINARY-001",
        ["ProviderAcceptance:Cloudinary:AcceptedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
        ["ProviderAcceptance:Cloudinary:EvidenceReference"] = "CLOUDINARY-TEST-001",
        ["ProviderAcceptance:MonnifySandbox:CredentialRotationReference"] = "ROTATE-MONNIFY-001",
        ["ProviderAcceptance:MonnifySandbox:AcceptedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
        ["ProviderAcceptance:MonnifySandbox:EvidenceReference"] = "MONNIFY-SANDBOX-001",
        ["ProviderAcceptance:MonnifyWebhook:CredentialRotationReference"] = "ROTATE-MONNIFY-001",
        ["ProviderAcceptance:MonnifyWebhook:AcceptedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
        ["ProviderAcceptance:MonnifyWebhook:EvidenceReference"] = "MONNIFY-WEBHOOK-001",
        ["ProviderAcceptance:MonnifyLivePaymentAndRefund:CredentialRotationReference"] = "ROTATE-MONNIFY-001",
        ["ProviderAcceptance:MonnifyLivePaymentAndRefund:AcceptedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
        ["ProviderAcceptance:MonnifyLivePaymentAndRefund:EvidenceReference"] = "MONNIFY-LIVE-001",
        ["ProviderAcceptance:PciResponsibilityReview:CredentialRotationReference"] = "PROVIDER-AOC-001",
        ["ProviderAcceptance:PciResponsibilityReview:AcceptedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
        ["ProviderAcceptance:PciResponsibilityReview:EvidenceReference"] = "PCI-SCOPE-001",
        ["ProviderAcceptance:HostedPaymentPageOnly"] = "true",
        ["DATABASE_RUNTIME_ROLE"] = "moore_runtime",
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
        ["HotelSettings:Name"] = "Moore Hotels Test",
        ["HotelSettings:Tagline"] = "Test hospitality",
        ["HotelSettings:Address"] = "1 Test Street, Abuja",
        ["HotelSettings:SupportEmail"] = "support@example.test",
        ["HotelSettings:Phone"] = "+2340000000000",
        ["HotelSettings:TimeZoneId"] = "Africa/Lagos",
        ["HotelSettings:CheckInHour"] = "14",
        ["HotelSettings:CheckOutHour"] = "12",
        ["SeedAdmin"] = "false"
    };

    private static Dictionary<string, string?> LocalSettings()
    {
        var settings = BaselineSettings();
        settings["ConnectionStrings:DefaultConnection"] =
            "Host=127.0.0.1;Port=5433;Database=moore_hotels_local;Username=postgres;Password=local-secret;SSL Mode=Disable";
        settings["Database:Provider"] = "PostgreSql";
        settings["Database:CreateIfMissing"] = "true";
        settings["Database:ApplyMigrationsOnStartup"] = "true";
        settings["Jwt:Issuer"] = "MooreHotels.Local";
        settings["Jwt:Audience"] = "MooreHotels.LocalClients";
        settings["AllowedOrigins:0"] = "http://localhost:3001";
        settings["AllowedHosts"] = "localhost;127.0.0.1";
        settings["ForwardedHeaders:Enabled"] = "false";
        settings["ForwardedHeaders:TrustRenderEdge"] = "false";
        settings.Remove("ForwardedHeaders:KnownProxies:0");
        settings.Remove("ForwardedHeaders:KnownNetworks:0");
        settings["Runtime:EnableSwagger"] = "true";
        settings["Runtime:EnableExternalServices"] = "false";
        settings["Runtime:AutoConfirmEmail"] = "true";
        settings["Security:RequireStaffMfa"] = "false";
        settings["Privacy:RequirePolicyAcceptance"] = "false";
        settings["Privacy:PrivacyPolicyUrl"] = "http://localhost:3001/privacy";
        settings["Privacy:BookingTermsUrl"] = "http://localhost:3001/terms";
        settings["Privacy:EnableRetentionWorker"] = "false";
        settings["Pricing:RequireQuoteForBooking"] = "false";
        settings["EmailSettings:DeliveryMode"] = "Capture";
        settings["MonnifySettings:Enabled"] = "false";
        settings["PublicAppUrl"] = "http://localhost:3001";
        settings["DashboardUrl"] = "http://localhost:3000";
        settings["Api:PublicBaseUrl"] = "http://localhost:5222";
        return settings;
    }

    private static IHostEnvironment ProductionEnvironment() =>
        new TestHostEnvironment
        {
            EnvironmentName = Environments.Production,
            ApplicationName = "MooreHotels.IntegrationTests",
            ContentRootPath = Directory.GetCurrentDirectory(),
            ContentRootFileProvider =
                new NullFileProvider()
        };

    private static IHostEnvironment LocalEnvironment() =>
        new TestHostEnvironment
        {
            EnvironmentName = AppEnvironments.Local,
            ApplicationName = "MooreHotels.IntegrationTests",
            ContentRootPath = Directory.GetCurrentDirectory(),
            ContentRootFileProvider = new NullFileProvider()
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
