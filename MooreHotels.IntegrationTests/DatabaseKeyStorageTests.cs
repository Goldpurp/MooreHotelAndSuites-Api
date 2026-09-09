using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Extensions;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class DatabaseKeyStorageTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task Encrypted_database_keys_survive_replacement_and_reject_wrong_certificate()
    {
        var connection = await fixture.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        using var certificate = CreateCertificate();
        using var wrongCertificate = CreateCertificate();
        await using var writer = CreateProvider(connection, certificate);
        CheckReadiness(writer);
        var payload = writer.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("MooreHotels.EmailOutbox.Payload.v1").Protect("restart recovery sentinel");
        var keyXml = await fixture.WithDbAsync(db => db.DataProtectionKeys.Select(key => key.Xml!).ToListAsync());
        Assert.NotEmpty(keyXml);
        Assert.All(keyXml, xml => Assert.Contains("EncryptedData", xml, StringComparison.Ordinal));
        Assert.DoesNotContain(keyXml, xml => xml.Contains("<probe", StringComparison.Ordinal));

        // A completely independent service provider, with no filesystem state.
        await using var replacement = CreateProvider(connection, certificate);
        CheckReadiness(replacement);
        Assert.Equal("restart recovery sentinel", replacement.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("MooreHotels.EmailOutbox.Payload.v1").Unprotect(payload));

        await using var invalid = CreateProvider(connection, wrongCertificate);
        var failure = Assert.Throws<TargetInvocationException>(() => CheckReadiness(invalid));
        Assert.IsAssignableFrom<CryptographicException>(failure.InnerException);
        Assert.Equal(keyXml, await fixture.WithDbAsync(db => db.DataProtectionKeys.Select(key => key.Xml!).ToListAsync()));
    }

    private static void CheckReadiness(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        typeof(WebApplicationExtensions).GetMethod("VerifyDataProtectionReadiness", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [scope.ServiceProvider]);
    }

    private static ServiceProvider CreateProvider(string connection, X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DataProtection:StorageProvider"] = "Database" }).Build());
        services.AddDbContext<MooreHotelsDbContext>(options => options.UseNpgsql(connection));
        services.AddDataProtection().SetApplicationName("MooreHotels.production")
            .PersistKeysToDbContext<MooreHotelsDbContext>().ProtectKeysWithCertificate(certificate);
        return services.BuildServiceProvider();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Moore key recovery test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
