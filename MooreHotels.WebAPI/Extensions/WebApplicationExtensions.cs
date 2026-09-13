using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
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
            var databaseKeys = string.Equals(app.Configuration["DataProtection:StorageProvider"], "Database", StringComparison.OrdinalIgnoreCase);
            if (app.Environment.IsDeployed() && !databaseKeys)
            {
                VerifyDataProtectionReadiness(scope.ServiceProvider);
            }

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
            await VerifyDatabaseEnvironmentBoundaryAsync(
                context,
                app.Environment.ToClientName(),
                allowMissing: app.Environment.IsLocal());
            if (database.ApplyMigrationsOnStartup)
            {
                await context.Database.MigrateAsync();
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Automatic migrations are disabled for {Environment}; checking identity bootstrap only.",
                        app.Environment.EnvironmentName);
                }
                if (!await context.Database.CanConnectAsync())
                {
                    throw new InvalidOperationException("The configured database is not reachable.");
                }
            }

            if (app.Environment.IsLocal())
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO public.environment_boundaries ("Id", "EnvironmentName", "BoundAtUtc")
                     VALUES (1, {app.Environment.ToClientName()}, CURRENT_TIMESTAMP)
                     ON CONFLICT ("Id") DO NOTHING
                     """);
            }
            await VerifyDatabaseEnvironmentBoundaryAsync(
                context,
                app.Environment.ToClientName(),
                allowMissing: false);

            if (app.Environment.IsDeployed())
            {
                await EnsureRuntimeDatabaseRoleIsLeastPrivilegedAsync(
                    context,
                    app.Configuration["DATABASE_RUNTIME_ROLE"]!);
                if (databaseKeys)
                    VerifyDataProtectionReadiness(scope.ServiceProvider);
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
            if (logger.IsEnabled(LogLevel.Critical))
            {
                logger.LogCritical(
                    "Database initialization failed with {ExceptionType}; the API will not start.",
                    exception.GetType().Name);
            }
            throw;
        }
    }

    private static async Task VerifyDatabaseEnvironmentBoundaryAsync(
        MooreHotelsDbContext context,
        string expectedEnvironment,
        bool allowMissing)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var tableCommand = context.Database.GetDbConnection().CreateCommand();
            tableCommand.CommandText =
                "SELECT to_regclass('public.environment_boundaries') IS NOT NULL";
            var tableExists = (bool)(await tableCommand.ExecuteScalarAsync() ?? false);
            if (!tableExists)
            {
                if (allowMissing) return;
                throw new InvalidOperationException(
                    "The database has not been bound to an application environment.");
            }

            await using var boundaryCommand = context.Database.GetDbConnection().CreateCommand();
            boundaryCommand.CommandText =
                "SELECT \"EnvironmentName\" FROM public.environment_boundaries WHERE \"Id\" = 1";
            var actualEnvironment = await boundaryCommand.ExecuteScalarAsync() as string;
            if (string.IsNullOrWhiteSpace(actualEnvironment))
            {
                if (allowMissing) return;
                throw new InvalidOperationException(
                    "The database environment boundary is missing.");
            }
            if (!string.Equals(
                    actualEnvironment,
                    expectedEnvironment,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The database is bound to {actualEnvironment}, not {expectedEnvironment}; startup is blocked.");
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static void VerifyDataProtectionReadiness(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (string.Equals(configuration["DataProtection:StorageProvider"], "Database", StringComparison.OrdinalIgnoreCase))
        {
            var context = services.GetRequiredService<MooreHotelsDbContext>();
            context.Database.CreateExecutionStrategy().Execute(() =>
            {
                using var transaction = context.Database.BeginTransaction();
                context.Database.ExecuteSqlRaw(
                    "INSERT INTO public.data_protection_keys (\"FriendlyName\", \"Xml\") VALUES ('startup-write-probe', '<probe />')");
                transaction.Rollback();
            });
        }
        else
        {
            // Existing keys may decrypt on a read-only mount while rotation fails.
            var keysPath = configuration["DataProtection:KeysPath"]!;
            Directory.CreateDirectory(keysPath);
            var probePath = Path.Combine(keysPath, $".write-probe-{Guid.NewGuid():N}");
            using var probe = new FileStream(
                probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
            probe.Flush(flushToDisk: true);
        }

        // Otherwise a different valid certificate can make Data Protection
        // create a new key while silently abandoning old tokens/outbox data.
        foreach (var key in services.GetRequiredService<IKeyManager>().GetAllKeys()
                     .Where(key => !key.IsRevoked))
        {
            if (key.CreateEncryptor() is null)
                throw new InvalidOperationException("An existing Data Protection key is unreadable.");
        }

        var protector = services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("MooreHotels.ProductionStartupReadiness.v1");
        var challenge = Guid.NewGuid().ToString("N");
        var protectedChallenge = protector.Protect(challenge);
        if (!string.Equals(
                protector.Unprotect(protectedChallenge),
                challenge,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The production Data Protection key ring failed its startup round-trip.");
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
                    EXISTS (
                        SELECT 1
                        FROM pg_roles inherited_role
                        WHERE inherited_role.oid <> r.oid
                          AND pg_has_role(current_user, inherited_role.oid, 'USAGE')
                          AND (
                              inherited_role.rolsuper OR inherited_role.rolcreaterole OR
                              inherited_role.rolcreatedb OR inherited_role.rolreplication OR
                              inherited_role.rolbypassrls OR
                              EXISTS (
                                  SELECT 1
                                  FROM pg_class inherited_object
                                  JOIN pg_namespace inherited_schema
                                    ON inherited_schema.oid = inherited_object.relnamespace
                                  WHERE inherited_schema.nspname = 'public'
                                    AND inherited_object.relowner = inherited_role.oid
                              ) OR
                              EXISTS (
                                  SELECT 1
                                  FROM pg_proc inherited_function
                                  JOIN pg_namespace inherited_schema
                                    ON inherited_schema.oid = inherited_function.pronamespace
                                  WHERE inherited_schema.nspname = 'public'
                                    AND inherited_function.proowner = inherited_role.oid
                              )
                          )
                    ),
                    EXISTS (
                        SELECT 1
                        FROM pg_proc owned_function
                        JOIN pg_namespace owned_schema ON owned_schema.oid = owned_function.pronamespace
                        WHERE owned_schema.nspname = 'public'
                          AND owned_function.proowner = r.oid
                    ),
                    to_regclass('public."__EFMigrationsHistory"') IS NULL OR
                        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'SELECT') OR
                        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'INSERT') OR
                        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'UPDATE') OR
                        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'DELETE'),
                    to_regclass('public.bookings') IS NULL OR
                        has_table_privilege(current_user, 'public.bookings', 'DELETE'),
                    to_regclass('public.data_protection_keys') IS NULL OR
                        NOT has_table_privilege(current_user, 'public.data_protection_keys', 'SELECT') OR
                        NOT has_table_privilege(current_user, 'public.data_protection_keys', 'INSERT') OR
                        has_table_privilege(current_user, 'public.data_protection_keys', 'UPDATE') OR
                        has_table_privilege(current_user, 'public.data_protection_keys', 'DELETE') OR
                        has_table_privilege(current_user, 'public.data_protection_keys', 'TRUNCATE') OR
                    to_regclass('public.audit_logs') IS NULL OR
                        has_table_privilege(current_user, 'public.audit_logs', 'UPDATE') OR
                        has_table_privilege(current_user, 'public.audit_logs', 'DELETE'),
                    to_regclass('public.environment_boundaries') IS NULL OR
                        NOT has_table_privilege(current_user, 'public.environment_boundaries', 'SELECT') OR
                        has_table_privilege(current_user, 'public.environment_boundaries', 'INSERT') OR
                        has_table_privilege(current_user, 'public.environment_boundaries', 'UPDATE') OR
                        has_table_privilege(current_user, 'public.environment_boundaries', 'DELETE') OR
                        has_table_privilege(current_user, 'public.environment_boundaries', 'TRUNCATE') OR
                        has_table_privilege(current_user, 'public.environment_boundaries', 'REFERENCES') OR
                        has_table_privilege(current_user, 'public.environment_boundaries', 'TRIGGER') OR
                        (pg_get_serial_sequence('public.environment_boundaries', 'Id') IS NOT NULL AND
                         (has_sequence_privilege(current_user, pg_get_serial_sequence('public.environment_boundaries', 'Id'), 'USAGE') OR
                          has_sequence_privilege(current_user, pg_get_serial_sequence('public.environment_boundaries', 'Id'), 'SELECT') OR
                          has_sequence_privilege(current_user, pg_get_serial_sequence('public.environment_boundaries', 'Id'), 'UPDATE'))),
                    current_user
                FROM pg_roles r
                WHERE r.rolname = current_user
                """;
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("The runtime PostgreSQL role could not be inspected.");

            var hasDangerousPrivilege = Enumerable.Range(0, 15)
                .Any(index => reader.GetBoolean(index));
            var actualRuntimeRole = reader.GetString(15);
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
                    "The production runtime PostgreSQL role has ownership, inherited administration, schema-change, migration-history, retained-record deletion, or audit-mutation access.");
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
            if (logger.IsEnabled(LogLevel.Information))
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
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Local database {DatabaseName} created.", databaseName);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // A second Local API instance may create it between the existence
            // check and CREATE DATABASE. The outcome is already the desired one.
            if (logger.IsEnabled(LogLevel.Information))
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
        app.UseMiddleware<LaunchGateMiddleware>();
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
