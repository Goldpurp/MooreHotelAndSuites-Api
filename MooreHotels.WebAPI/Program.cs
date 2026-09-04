using MooreHotels.WebAPI.Configuration;
using MooreHotels.WebAPI.Extensions;

var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? AppEnvironments.Production;

ConfigurationBootstrap.LoadEnvironmentFile(environmentName);

// Render exposes pre-deploy variables to the service container as well. The
// migration owner is required by ./migrate, never by the API process, so remove
// it after profile loading but before configuration is built or request-serving
// components are created. Runtime uses only DefaultConnection.
Environment.SetEnvironmentVariable("MIGRATION_CONNECTION_STRING", null);

var builder = WebApplication.CreateBuilder(args);
ConfigurationBootstrap.ValidateForStartup(builder.Configuration, builder.Environment);

builder.Services.AddMooreHotelsApi(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.InitializeDatabaseAsync();
app.UseMooreHotelsPipeline();

await app.RunAsync();

public partial class Program;
