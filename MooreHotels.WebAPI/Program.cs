using MooreHotels.WebAPI.Configuration;
using MooreHotels.WebAPI.Extensions;

var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? AppEnvironments.Production;

ConfigurationBootstrap.LoadEnvironmentFile(environmentName);

var builder = WebApplication.CreateBuilder(args);
ConfigurationBootstrap.ValidateForStartup(builder.Configuration, builder.Environment);

builder.Services.AddMooreHotelsApi(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.InitializeDatabaseAsync();
app.UseMooreHotelsPipeline();

await app.RunAsync();

public partial class Program;
