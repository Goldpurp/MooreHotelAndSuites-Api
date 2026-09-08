using MooreHotels.WebAPI.Configuration;
using MooreHotels.WebAPI.Extensions;

var environmentName = AppEnvironments.ResolveProcessEnvironment();

ConfigurationBootstrap.LoadEnvironmentFile(environmentName);

if (string.Equals(environmentName, AppEnvironments.Local, StringComparison.Ordinal))
{
    // Local never receives live provider credentials. Integration tests can
    // retain fake Monnify values only when they explicitly inject test doubles.
    Environment.SetEnvironmentVariable("EmailSettings__ApiPass", null);
    Environment.SetEnvironmentVariable("CloudinarySettings__ApiKey", null);
    Environment.SetEnvironmentVariable("CloudinarySettings__ApiSecret", null);
    if (!string.Equals(
            Environment.GetEnvironmentVariable("Runtime__AllowLocalProviderTestDoubles"),
            "true",
            StringComparison.OrdinalIgnoreCase))
    {
        Environment.SetEnvironmentVariable("MonnifySettings__ApiKey", null);
        Environment.SetEnvironmentVariable("MonnifySettings__SecretKey", null);
        Environment.SetEnvironmentVariable("MonnifySettings__ContractCode", null);
    }
}

// Render exposes pre-deploy variables to the service container as well. The
// migration owner is required by ./migrate, never by the API process, so remove
// it after profile loading but before configuration is built or request-serving
// components are created. The container entrypoint also removes these before
// exec so they cannot remain in Linux's initial process environment snapshot.
// Runtime uses only DefaultConnection.
Environment.SetEnvironmentVariable("MIGRATION_CONNECTION_STRING", null);
Environment.SetEnvironmentVariable("DATABASE_RUNTIME_PASSWORD", null);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = environmentName
});
ConfigurationBootstrap.ValidateForStartup(builder.Configuration, builder.Environment);

builder.Services.AddMooreHotelsApi(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.InitializeDatabaseAsync();
app.UseMooreHotelsPipeline();

await app.RunAsync();

public partial class Program;
