using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Infrastructure.Persistence;
using Npgsql;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class FreshDatabaseMigrationTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task IdempotentScript_CreatesAnEmptyDatabase_AndCanBeReplayed()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var source = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        Assert.StartsWith("moore_hotels_transfer_tests_", source.Database);

        var databaseName = $"moore_migration_tests_{Guid.NewGuid():N}";
        var target = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = databaseName,
            Pooling = false
        };
        var maintenance = new NpgsqlConnectionStringBuilder(target.ConnectionString)
        {
            Database = "postgres"
        };

        await using var admin = new NpgsqlConnection(maintenance.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var script = db.GetService<IMigrator>().GenerateScript(
                options: MigrationsSqlGenerationOptions.Idempotent);
            await using (var connection = new NpgsqlConnection(target.ConnectionString))
            {
                await connection.OpenAsync();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await using var migrate = new NpgsqlCommand(script, connection) { CommandTimeout = 120 };
                    await migrate.ExecuteNonQueryAsync();
                }
            }

            await using var fresh = new MooreHotelsDbContext(
                new DbContextOptionsBuilder<MooreHotelsDbContext>()
                    .UseNpgsql(target.ConnectionString).Options);
            Assert.Equal(db.Database.GetMigrations(), await fresh.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await fresh.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await fresh.Bookings.CountAsync());
            Assert.Equal(0, await fresh.Users.CountAsync());
            Assert.Equal(0, await fresh.Rooms.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
