using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using DotNetEnv;
using MooreHotels.Application.DTOs;
using Npgsql;

namespace MooreHotels.WebAPI.Configuration;

public static class AppEnvironments
{
    public const string Local = "Local";
    public const string Production = "Production";

    public static bool IsLocal(this IHostEnvironment environment) =>
        environment.IsEnvironment(Local);

    public static bool IsDeployed(this IHostEnvironment environment) =>
        environment.IsProduction();

    public static string ToClientName(this IHostEnvironment environment) =>
        environment.IsLocal() ? "local" : "production";

    public static void EnsureSupported(string environmentName)
    {
        if (!string.Equals(environmentName, Local, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(environmentName, Production, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported ASPNETCORE_ENVIRONMENT '{environmentName}'. " +
                $"Only '{Local}' and '{Production}' are supported.");
        }
    }
}

public sealed class RuntimeSettings
{
    public bool EnableSwagger { get; init; }
    public bool EnableExternalServices { get; init; } = true;
    public bool AutoConfirmEmail { get; init; }
    public bool UseHttpsRedirection { get; init; }
    public bool ResponseCompression { get; init; } = true;
    public bool EnableBookingExpiration { get; init; } = true;
    public int ExternalRequestTimeoutSeconds { get; init; } = 20;
}

public sealed class DatabaseSettings
{
    public bool CreateIfMissing { get; init; }
    public bool ApplyMigrationsOnStartup { get; init; }
    public int MaxRetryCount { get; init; } = 3;
    public int CommandTimeoutSeconds { get; init; } = 30;
    public int ContextPoolSize { get; init; } = 64;
}

public sealed class ForwardedHeadersSettings
{
    public bool Enabled { get; init; }
    public bool TrustRenderEdge { get; init; }
    public int ForwardLimit { get; init; } = 1;
    public string[] KnownProxies { get; init; } = [];
    public string[] KnownNetworks { get; init; } = [];
}

public sealed class JwtSettings
{
    public string Key { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public int ExpirationMinutes { get; init; } = 60;
}

public sealed class BankTransferSettings
{
    public string BankName { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
}

public static class ConfigurationBootstrap
{
    public static void LoadEnvironmentFile(string environmentName)
    {
        AppEnvironments.EnsureSupported(environmentName);

        // Production credentials are supplied by the hosting platform's secret
        // manager. Only Local reads a dotenv file from disk.
        if (!string.Equals(environmentName, AppEnvironments.Local, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        const string fileName = ".env.local";

        // Resolve the solution root first. A process launched by `dotnet run`, EF
        // tooling, an IDE, or the compiled DLL can have a different working
        // directory. Loading the closest dotenv file allowed stale project-level
        // files to silently select a different database.
        var solutionRoot = FindSolutionRoot(Directory.GetCurrentDirectory())
            ?? FindSolutionRoot(AppContext.BaseDirectory);
        if (solutionRoot is not null)
        {
            var candidate = Path.Combine(solutionRoot.FullName, fileName);
            if (File.Exists(candidate))
            {
                // Values supplied by the shell, CI, or cloud secret manager win.
                Env.NoClobber().Load(candidate);
                return;
            }
        }

        // This fallback supports a published Local build placed outside the
        // solution directory. Production never reads a dotenv file from disk.
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                Env.NoClobber().Load(candidate);
                return;
            }

            directory = directory.Parent;
        }
    }

    private static DirectoryInfo? FindSolutionRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MooreHotels.sln")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static void ValidateForStartup(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var errors = new List<string>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        var jwt = configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
        var runtime = configuration.GetSection("Runtime").Get<RuntimeSettings>() ?? new RuntimeSettings();
        var origins = configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
        var allowedHosts = configuration["AllowedHosts"] ?? string.Empty;
        var forwardedHeaders = configuration.GetSection("ForwardedHeaders")
            .Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        var monnify = configuration.GetSection("MonnifySettings")
            .Get<MooreHotels.Domain.Common.MonnifySettings>()
            ?? new MooreHotels.Domain.Common.MonnifySettings();
        var email = configuration.GetSection("EmailSettings")
            .Get<EmailSettings>() ?? new EmailSettings();

        if (IsMissingOrPlaceholder(connectionString))
        {
            errors.Add("ConnectionStrings:DefaultConnection is required.");
        }
        else
        {
            try
            {
                var database = new NpgsqlConnectionStringBuilder(connectionString);
                if (environment.IsDeployed())
                {
                    var databaseHost = database.Host;
                    if (!string.IsNullOrWhiteSpace(databaseHost) && IsLoopback(databaseHost))
                    {
                        errors.Add("Production cannot use a loopback database host.");
                    }

                    if (database.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
                    {
                        errors.Add("Production PostgreSQL must use SSL Mode=Require, VerifyCA, or VerifyFull.");
                    }
                }
            }
            catch (ArgumentException)
            {
                errors.Add("ConnectionStrings:DefaultConnection is not a valid Npgsql connection string.");
            }
        }

        if (IsMissingOrPlaceholder(jwt.Key) || System.Text.Encoding.UTF8.GetByteCount(jwt.Key) < 32)
        {
            errors.Add("Jwt:Key must be a non-placeholder secret containing at least 32 UTF-8 bytes.");
        }

        if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
        {
            errors.Add("Jwt:Issuer and Jwt:Audience are required.");
        }

        if (jwt.ExpirationMinutes is < 5 or > 1440)
        {
            errors.Add("Jwt:ExpirationMinutes must be between 5 and 1440.");
        }

        if (origins.Length == 0)
        {
            errors.Add("At least one AllowedOrigins entry is required.");
        }

        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.AbsolutePath != "/")
            {
                errors.Add($"Allowed origin '{origin}' must contain only an HTTP(S) origin (no path).");
                continue;
            }

            if (environment.IsDeployed() &&
                (uri.Scheme != Uri.UriSchemeHttps || IsLoopback(uri.Host)))
            {
                errors.Add($"Production origin '{origin}' must be a non-loopback HTTPS origin.");
            }
        }

        if (environment.IsDeployed() &&
            (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Split(';').Any(host => host.Trim() == "*")))
        {
            errors.Add("Production AllowedHosts must contain explicit host names, not '*'.");
        }

        if (environment.IsDeployed())
        {
            if (!forwardedHeaders.Enabled)
            {
                errors.Add("ForwardedHeaders:Enabled must be true behind the production reverse proxy.");
            }
            else if (!forwardedHeaders.TrustRenderEdge &&
                     forwardedHeaders.KnownProxies.Length == 0 &&
                     forwardedHeaders.KnownNetworks.Length == 0)
            {
                errors.Add(
                    "At least one trusted ForwardedHeaders:KnownProxies or KnownNetworks entry is required unless TrustRenderEdge is enabled.");
            }

            if (forwardedHeaders.ForwardLimit is < 1 or > 3)
            {
                errors.Add("ForwardedHeaders:ForwardLimit must be between 1 and 3.");
            }
            else if (forwardedHeaders.TrustRenderEdge &&
                     forwardedHeaders.ForwardLimit != 1)
            {
                errors.Add(
                    "ForwardedHeaders:ForwardLimit must be exactly 1 when TrustRenderEdge is enabled.");
            }

            foreach (var proxy in forwardedHeaders.KnownProxies)
            {
                if (!IPAddress.TryParse(proxy, out _))
                {
                    errors.Add($"ForwardedHeaders known proxy '{proxy}' is not a valid IP address.");
                }
            }

            foreach (var network in forwardedHeaders.KnownNetworks)
            {
                if (!IsValidCidr(network))
                {
                    errors.Add($"ForwardedHeaders known network '{network}' is not valid CIDR notation.");
                }
                else if (int.TryParse(
                             network.Split(
                                 '/',
                                 StringSplitOptions.TrimEntries |
                                 StringSplitOptions.RemoveEmptyEntries)[1],
                             out var prefixLength) &&
                         prefixLength == 0)
                {
                    errors.Add(
                        $"ForwardedHeaders known network '{network}' trusts every address and is not permitted.");
                }
            }
        }

        if (environment.IsDeployed() && runtime.EnableSwagger)
        {
            errors.Add("Swagger must remain disabled in Production unless a separate protected documentation deployment is used.");
        }

        if (environment.IsDeployed() && runtime.AutoConfirmEmail)
        {
            errors.Add("Runtime:AutoConfirmEmail must remain disabled in a deployed environment.");
        }

        if (environment.IsDeployed() && !runtime.EnableExternalServices)
        {
            errors.Add(
                "Runtime:EnableExternalServices must be true in Production.");
        }

        ValidateEmailSettings(email, environment, errors);

        if (environment.IsDeployed() &&
            IsMissingOrPlaceholder(configuration["DataProtection:KeysPath"]))
        {
            errors.Add("Production DataProtection:KeysPath must point to persistent encrypted storage so account links survive restarts.");
        }
        if (environment.IsDeployed())
        {
            RequireSecret(configuration, "DataProtection:CertificatePath", errors);
            RequireSecret(configuration, "DataProtection:CertificatePassword", errors);
        }

        if (environment.IsDeployed())
        {
            RequireSecret(configuration, "CloudinarySettings:CloudName", errors);
            RequireSecret(configuration, "CloudinarySettings:ApiKey", errors);
            RequireSecret(configuration, "CloudinarySettings:ApiSecret", errors);
            RequireSecret(configuration, "EmailSettings:ApiPass", errors);
            RequireSecret(configuration, "EmailSettings:SenderEmail", errors);
            RequireSecret(configuration, "EmailSettings:AdminNotificationEmail", errors);
            if (monnify.Enabled)
            {
                RequireSecret(configuration, "MonnifySettings:ApiKey", errors);
                RequireSecret(configuration, "MonnifySettings:SecretKey", errors);
                RequireSecret(configuration, "MonnifySettings:ContractCode", errors);
            }
        }

        if (environment.IsDeployed())
        {
            RequireSecret(configuration, "BankTransferSettings:BankName", errors);
            RequireSecret(configuration, "BankTransferSettings:AccountName", errors);
            RequireSecret(configuration, "BankTransferSettings:AccountNumber", errors);
            ValidateDeployedUrl(configuration, "PublicAppUrl", errors);
            ValidateDeployedUrl(configuration, "DashboardUrl", errors);
            ValidateDeployedUrl(configuration, "Api:PublicBaseUrl", errors);
            if (monnify.Enabled)
            {
                ValidateDeployedUrl(configuration, "MonnifySettings:BaseUrl", errors);
                ValidateProductionMonnifySettings(monnify, errors);
            }
        }

        if (configuration.GetValue<bool>("SeedAdmin"))
        {
            RequireSecret(configuration, "AdminSeed:Email", errors);
            RequireSecret(configuration, "AdminSeed:Password", errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Application configuration is invalid:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => $" - {error}")));
        }
    }

    private static void RequireSecret(IConfiguration configuration, string key, ICollection<string> errors)
    {
        if (IsMissingOrPlaceholder(configuration[key]))
        {
            errors.Add($"{key} is required and cannot be a placeholder.");
        }
    }

    private static void ValidateDeployedUrl(
        IConfiguration configuration,
        string key,
        ICollection<string> errors)
    {
        var value = configuration[key];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || IsLoopback(uri.Host))
        {
            errors.Add($"{key} must be a non-loopback HTTPS URL in a deployed environment.");
        }
    }

    private static bool IsMissingOrPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Contains("<SET", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(value, "<[^>]+>", RegexOptions.CultureInvariant);

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static void ValidateEmailSettings(
        EmailSettings settings,
        IHostEnvironment environment,
        ICollection<string> errors)
    {
        var usesBrevo = string.Equals(
            settings.DeliveryMode,
            "Brevo",
            StringComparison.OrdinalIgnoreCase);
        var usesCapture = string.Equals(
            settings.DeliveryMode,
            "Capture",
            StringComparison.OrdinalIgnoreCase);

        if (!usesBrevo && !usesCapture)
        {
            errors.Add(
                "EmailSettings:DeliveryMode must be either 'Brevo' or 'Capture'.");
            return;
        }

        if (usesCapture && !environment.IsLocal())
        {
            errors.Add(
                "EmailSettings:DeliveryMode=Capture is permitted only in Local and automated-test environments.");
        }

        if (usesBrevo)
        {
            if (IsMissingOrPlaceholder(settings.ApiPass))
            {
                errors.Add(
                    "EmailSettings:ApiPass is required for Brevo delivery.");
            }

            if (IsMissingOrPlaceholder(settings.SenderEmail) ||
                !MailAddress.TryCreate(settings.SenderEmail, out _))
            {
                errors.Add(
                    "EmailSettings:SenderEmail must be a valid Brevo-verified sender address.");
            }

            if (string.IsNullOrWhiteSpace(settings.SenderName))
            {
                errors.Add("EmailSettings:SenderName is required.");
            }

            if (IsMissingOrPlaceholder(settings.AdminNotificationEmail) ||
                !MailAddress.TryCreate(
                    settings.AdminNotificationEmail,
                    out _))
            {
                errors.Add(
                    "EmailSettings:AdminNotificationEmail must be a valid address.");
            }
        }

        if (settings.MaxRetryAttempts is < 1 or > 5)
        {
            errors.Add(
                "EmailSettings:MaxRetryAttempts must be between 1 and 5.");
        }
    }

    private static void ValidateProductionMonnifySettings(
        MooreHotels.Domain.Common.MonnifySettings settings,
        ICollection<string> errors)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            !baseUri.Host.Equals("api.monnify.com", StringComparison.OrdinalIgnoreCase) ||
            !baseUri.IsDefaultPort ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            baseUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            errors.Add(
                "Production MonnifySettings:BaseUrl must be exactly https://api.monnify.com.");
        }

        if (!settings.EnforceWebhookIpAllowlist)
        {
            errors.Add(
                "Production MonnifySettings:EnforceWebhookIpAllowlist must be true.");
        }

        if (settings.AllowedWebhookIpAddresses.Length == 0)
        {
            errors.Add(
                "At least one MonnifySettings:AllowedWebhookIpAddresses entry is required.");
        }
        else
        {
            foreach (var address in settings.AllowedWebhookIpAddresses)
            {
                if (!IPAddress.TryParse(address, out _))
                {
                    errors.Add(
                        $"Monnify webhook source '{address}' is not a valid IP address.");
                }
            }

            var officialAddress = IPAddress.Parse("35.242.133.146");
            var configuredAddresses = settings.AllowedWebhookIpAddresses
                .Select(value =>
                    IPAddress.TryParse(value, out var parsed)
                        ? parsed.MapToIPv4()
                        : null)
                .Where(value => value is not null)
                .Distinct()
                .ToArray();
            if (configuredAddresses.Length != 1 ||
                !configuredAddresses[0]!.Equals(officialAddress))
            {
                errors.Add(
                    "Production MonnifySettings:AllowedWebhookIpAddresses must contain only Monnify's documented address 35.242.133.146.");
            }
        }
    }

    private static bool IsValidCidr(string value)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var maximumPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return prefixLength >= 0 && prefixLength <= maximumPrefix;
    }
}
