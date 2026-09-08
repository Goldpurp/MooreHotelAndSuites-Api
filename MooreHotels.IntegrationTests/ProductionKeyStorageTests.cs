using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MooreHotels.WebAPI.Configuration;
using MooreHotels.WebAPI.Extensions;

namespace MooreHotels.IntegrationTests;

public sealed class ProductionKeyStorageTests
{
    [Fact]
    public async Task Production_checks_storage_even_when_an_existing_protector_works()
    {
        var root = Directory.CreateTempSubdirectory("moore-key-storage-");
        try
        {
            var blockedPath = Path.Combine(root.FullName, "not-a-directory");
            await File.WriteAllTextAsync(blockedPath, "occupied");
            await using var app = CreateApp(blockedPath, root.FullName);

            // A working protector alone must not let invalid persistent storage
            // pass startup. The failure must occur before database resolution.
            await Assert.ThrowsAnyAsync<IOException>(() => app.InitializeDatabaseAsync());
            Assert.Equal("occupied", await File.ReadAllTextAsync(blockedPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Production_cleans_up_its_storage_probe_before_database_initialization()
    {
        var root = Directory.CreateTempSubdirectory("moore-key-storage-");
        try
        {
            await using var app = CreateApp(root.FullName, root.FullName);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => app.InitializeDatabaseAsync());

            // Intentionally omit a database: reaching this resolution proves
            // the storage and cryptographic checks completed successfully.
            Assert.Contains("MooreHotelsDbContext", error.Message);
            Assert.Empty(Directory.EnumerateFiles(root.FullName, ".write-probe-*"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Production_rejects_a_valid_certificate_that_cannot_read_existing_keys()
    {
        var root = Directory.CreateTempSubdirectory("moore-key-certificate-");
        try
        {
            using var originalCertificate = CreateCertificate();
            using var wrongCertificate = CreateCertificate();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection()
                .PersistKeysToFileSystem(root)
                .ProtectKeysWithCertificate(originalCertificate);
            using var writer = services.BuildServiceProvider();
            var protector = writer.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("certificate-recovery-test");
            var oldPayload = protector.Protect("previously-encrypted-payload");
            var originalKeys = Directory.GetFiles(root.FullName, "key-*.xml");
            Assert.Single(originalKeys);

            await using var app = CreateApp(root.FullName, root.FullName, wrongCertificate);
            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => app.InitializeDatabaseAsync());

            Assert.Equal(originalKeys, Directory.GetFiles(root.FullName, "key-*.xml"));
            Assert.Equal("previously-encrypted-payload", protector.Unprotect(oldPayload));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Disposable key storage test", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static WebApplication CreateApp(
        string keysPath,
        string keyRingPath,
        X509Certificate2? certificate = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:KeysPath"] = keysPath
        });
        builder.Services.Configure<DatabaseSettings>(_ => { });
        var protection = builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        if (certificate is not null)
            protection.ProtectKeysWithCertificate(certificate);
        else
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        return builder.Build();
    }
}
