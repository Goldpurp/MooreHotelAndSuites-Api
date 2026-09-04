using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using MooreHotels.Infrastructure.Hubs;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.Infrastructure.Seed;
using MooreHotels.WebAPI.Configuration;
using MooreHotels.WebAPI.Middleware;

namespace MooreHotels.WebAPI.Extensions;

public static class WebApplicationExtensions
{
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        var database = app.Services.GetRequiredService<IOptions<DatabaseSettings>>().Value;
        await using var scope = app.Services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DatabaseInitialization");

        try
        {
            if (database.CreateIfMissing)
            {
                if (!app.Environment.IsLocal())
                {
                    throw new InvalidOperationException(
                        "Database:CreateIfMissing is permitted only in the Local environment.");
                }

                await EnsureLocalDatabaseExistsAsync(app.Configuration, logger);
            }

            var context = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
            if (database.ApplyMigrationsOnStartup)
            {
                await context.Database.MigrateAsync();
            }
            else
            {
                logger.LogInformation(
                    "Automatic migrations are disabled for {Environment}; checking identity bootstrap only.",
                    app.Environment.EnvironmentName);
                if (!await context.Database.CanConnectAsync())
                {
                    throw new InvalidOperationException("The configured database is not reachable.");
                }
            }

            if (app.Environment.IsDeployed())
            {
                await EnsureRuntimeDatabaseRoleIsLeastPrivilegedAsync(
                    context,
                    app.Configuration["DATABASE_RUNTIME_ROLE"]!);
            }

            await DbInitializer.SeedRolesAsync(scope.ServiceProvider);

            if (app.Configuration.GetValue<bool>("SeedAdmin"))
            {
                await DbInitializer.SeedAdminAsync(scope.ServiceProvider);
            }

            logger.LogInformation("Database migrations and identity initialization completed.");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Database initialization failed; the API will not start.");
            throw;
        }
    }

    private static async Task EnsureRuntimeDatabaseRoleIsLeastPrivilegedAsync(
        MooreHotelsDbContext context,
        string expectedRuntimeRole)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                SELECT
                    r.rolsuper,
                    r.rolcreaterole,
                    r.rolcreatedb,
                    r.rolreplication,
                    r.rolbypassrls,
                    has_database_privilege(current_user, current_database(), 'CREATE'),
                    has_schema_privilege(current_user, 'public', 'CREATE'),
                    EXISTS (
                        SELECT 1
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'public'
                          AND c.relkind IN ('r', 'p', 'S')
                          AND pg_get_userbyid(c.relowner) = current_user
                    ),
                    EXISTS (
                        SELECT 1
                        FROM pg_roles powerful
                        WHERE powerful.rolname IN (
                            'pg_read_all_data',
                            'pg_write_all_data',
                            'pg_read_server_files',
                            'pg_write_server_files',
                            'pg_execute_server_program',
                            'pg_signal_backend'
                        )
                          AND pg_has_role(current_user, powerful.oid, 'MEMBER')
                    ),
                    current_user
                FROM pg_roles r
                WHERE r.rolname = current_user
                """;
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("The runtime PostgreSQL role could not be inspected.");

            var hasDangerousPrivilege = Enumerable.Range(0, 9)
                .Any(index => reader.GetBoolean(index));
            var actualRuntimeRole = reader.GetString(9);
            if (!string.Equals(
                    actualRuntimeRole,
                    expectedRuntimeRole,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The configured production database connection does not use DATABASE_RUNTIME_ROLE.");
            }
            if (hasDangerousPrivilege)
            {
                throw new InvalidOperationException(
                    "The production runtime PostgreSQL role must not own application objects or have superuser, role-management, database-creation, replication, BYPASSRLS, database CREATE, or public-schema CREATE privileges.");
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureLocalDatabaseExistsAsync(
        IConfiguration configuration,
        ILogger logger)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = target.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("The Local connection string must specify a database name.");
        }

        // Database creation needs an existing maintenance database. This
        // short-lived, non-pooled connection is used only during Local startup.
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
            Multiplexing = false,
            Enlist = false
        };

        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();

        await using var existsCommand = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @databaseName",
            connection);
        existsCommand.Parameters.AddWithValue("databaseName", databaseName);
        if (await existsCommand.ExecuteScalarAsync() is not null)
        {
            logger.LogInformation("Local database {DatabaseName} already exists.", databaseName);
            return;
        }

        // PostgreSQL cannot parameterize identifiers; double-quoting and
        // escaping make the configured database name safe as an identifier.
        var quotedDatabaseName = $"\"{databaseName.Replace("\"", "\"\"")}\"";
        try
        {
            await using var createCommand = new NpgsqlCommand(
                $"CREATE DATABASE {quotedDatabaseName}",
                connection);
            await createCommand.ExecuteNonQueryAsync();
            logger.LogInformation("Local database {DatabaseName} created.", databaseName);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // A second Local API instance may create it between the existence
            // check and CREATE DATABASE. The outcome is already the desired one.
            logger.LogInformation("Local database {DatabaseName} was created concurrently.", databaseName);
        }
    }

    public static WebApplication UseMooreHotelsPipeline(this WebApplication app)
    {
        var runtime = app.Services.GetRequiredService<IOptions<RuntimeSettings>>().Value;
        var forwardedHeaders = app.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;

        if (forwardedHeaders.Enabled)
        {
            app.UseForwardedHeaders();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<EnvironmentBoundaryMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        if (app.Environment.IsDeployed())
        {
            app.UseHsts();
        }

        if (runtime.UseHttpsRedirection)
        {
            app.UseHttpsRedirection();
        }

        if (runtime.ResponseCompression)
        {
            app.UseResponseCompression();
        }

        if (runtime.EnableSwagger)
        {
            app.UseStaticFiles();
            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "Moore Hotels API v1");
                options.DocumentTitle = $"Moore Hotels API ({app.Environment.EnvironmentName})";
                options.DisplayRequestDuration();
                options.DefaultModelsExpandDepth(-1);
                options.InjectStylesheet("/swagger-custom.css");
            });
        }

        app.UseRouting();
        app.UseCors(ServiceCollectionExtensions.FrontendCorsPolicy);
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseUserStatusEnforcement();
        app.UseAuthorization();

        app.MapGet("/robots.txt", () => Results.Text(
                "User-agent: *\nDisallow: /\n",
                "text/plain"))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/health/live", () => Results.Ok(new
        {
            status = "Healthy",
            timestamp = DateTimeOffset.UtcNow,
            environment = app.Environment.ToClientName()
        }))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapControllers();
        app.MapHub<NotificationHub>("/hubs/notifications");

        return app;
    }
}
