using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MooreHotels.Infrastructure.Persistence;

public sealed class MooreHotelsDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<MooreHotelsDbContext>
{
    public MooreHotelsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
                "ConnectionStrings__DefaultConnection")
            ?? ReadLocalConnectionString()
            ?? "Host=127.0.0.1;Database=moore_hotels_design_time;Username=postgres";

        var options = new DbContextOptionsBuilder<MooreHotelsDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly("MooreHotels.Infrastructure"))
            .Options;

        return new MooreHotelsDbContext(options);
    }

    private static string? ReadLocalConnectionString()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Local",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var solution = Path.Combine(directory.FullName, "MooreHotels.sln");
            var envFile = Path.Combine(directory.FullName, ".env.local");
            if (File.Exists(solution) && File.Exists(envFile))
            {
                const string key = "ConnectionStrings__DefaultConnection=";
                var line = File.ReadLines(envFile)
                    .FirstOrDefault(value => value.StartsWith(key, StringComparison.Ordinal));
                return line?[key.Length..].Trim().Trim('"');
            }

            directory = directory.Parent;
        }

        return null;
    }
}
