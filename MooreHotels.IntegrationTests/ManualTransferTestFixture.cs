using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Infrastructure.Identity;
using MooreHotels.Infrastructure.Persistence;
using Npgsql;

namespace MooreHotels.IntegrationTests;

public sealed record TestUser(Guid Id, UserRole Role, string Token);

public sealed class ManualTransferTestFixture : IAsyncLifetime
{
    private readonly Dictionary<string, string?> _originalEnvironment = new();
    private string _maintenanceConnectionString = string.Empty;
    private string _databaseName = string.Empty;
    private TestApiFactory? _factory;

    public FakeMonnifyService Monnify { get; } = new();
    public RecordingEmailService Email { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory!.Services;
    public TestUser Admin { get; private set; } = null!;
    public TestUser Manager { get; private set; } = null!;
    public TestUser Staff { get; private set; } = null!;
    public TestUser ClientUser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var localConnection = ReadLocalConnectionString();
        var target = new NpgsqlConnectionStringBuilder(localConnection);
        _databaseName = $"moore_hotels_transfer_tests_{Guid.NewGuid():N}"[..42];
        target.Database = _databaseName;
        target.Pooling = false;

        var maintenance = new NpgsqlConnectionStringBuilder(localConnection)
        {
            Database = "postgres",
            Pooling = false,
            Multiplexing = false,
            Enlist = false
        };
        _maintenanceConnectionString = maintenance.ConnectionString;

        await using (var connection = new NpgsqlConnection(_maintenanceConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE \"{_databaseName}\"",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Local");
        SetEnvironment("ConnectionStrings__DefaultConnection", target.ConnectionString);
        SetEnvironment("Jwt__Key", "MANUAL_TRANSFER_INTEGRATION_TEST_KEY_64_BYTES_LONG_0123456789ABCDEF");
        SetEnvironment("Jwt__Issuer", "MooreHotels.IntegrationTests");
        SetEnvironment("Jwt__Audience", "MooreHotels.IntegrationTests.Clients");
        SetEnvironment("Database__CreateIfMissing", "false");
        SetEnvironment("Database__ApplyMigrationsOnStartup", "true");
        // Keep retries enabled in tests so explicit transactions are exercised
        // with the same execution-strategy constraint used in Production.
        SetEnvironment("Database__MaxRetryCount", "2");
        SetEnvironment("Runtime__EnableExternalServices", "false");
        SetEnvironment("Runtime__EnableSwagger", "true");
        SetEnvironment("Runtime__EnableBookingExpiration", "false");
        SetEnvironment("MonnifySettings__Enabled", "true");
        SetEnvironment("EmailSettings__DeliveryMode", "Capture");
        SetEnvironment(
            "EmailSettings__AdminNotificationEmail",
            "admin-notifications@example.test");
        SetEnvironment("MonnifySettings__SecretKey", "integration-webhook-signing-secret");
        SetEnvironment("MonnifySettings__EnforceWebhookIpAllowlist", "false");
        SetEnvironment("SeedAdmin", "false");

        _factory = new TestApiFactory(Monnify, Email);
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost")
        });

        Admin = await SeedUserAsync(UserRole.Admin);
        Manager = await SeedUserAsync(UserRole.Manager);
        Staff = await SeedUserAsync(UserRole.Staff);
        ClientUser = await SeedUserAsync(UserRole.Client);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            try
            {
                await using var scope = Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
                var imageService = scope.ServiceProvider.GetRequiredService<IImageService>();
                var publicIds = await db.RoomImages
                    .AsNoTracking()
                    .Select(image => image.PublicId)
                    .Concat(db.Users
                        .AsNoTracking()
                        .Where(user => user.AvatarPublicId != null)
                        .Select(user => user.AvatarPublicId!))
                    .Distinct()
                    .ToListAsync();
                foreach (var publicId in publicIds)
                {
                    await imageService.DeleteImageAsync(publicId);
                }
            }
            catch
            {
                // Test database teardown must still run if asset cleanup fails.
            }
        }

        Client?.Dispose();
        _factory?.Dispose();
        NpgsqlConnection.ClearAllPools();

        if (!string.IsNullOrWhiteSpace(_maintenanceConnectionString))
        {
            await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var (key, value) in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public async Task<Booking> CreateBookingAsync(
        PaymentMethod paymentMethod = PaymentMethod.DirectTransfer,
        PaymentStatus paymentStatus = PaymentStatus.AwaitingVerification,
        BookingStatus bookingStatus = BookingStatus.Pending,
        DateTime? createdAtUtc = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var room = new Room
        {
            Id = Guid.NewGuid(),
            RoomNumber = $"T-{unique[..8]}",
            Name = "Integration Test Room",
            Category = RoomCategory.Standard,
            Floor = PropertyFloor.GroundFloor,
            Status = RoomStatus.Available,
            PricePerNight = 50000m,
            Capacity = 2,
            Size = "25 sqm",
            IsOnline = true,
            Description = "Manual transfer integration test room.",
            Amenities = ["Wi-Fi"]
        };
        var guest = new Guest
        {
            Id = $"GS-{unique[..16]}",
            FirstName = "Transfer",
            LastName = "Tester",
            Email = $"transfer-{unique[..12].ToLowerInvariant()}@example.test",
            Phone = "+2348000000000"
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            BookingCode = $"MHS-{unique[..12]}",
            RoomId = room.Id,
            GuestId = guest.Id,
            CheckIn = DateTime.UtcNow.AddDays(5),
            CheckOut = DateTime.UtcNow.AddDays(7),
            Status = bookingStatus,
            Amount = 100000m,
            PaymentStatus = paymentStatus,
            PaymentMethod = paymentMethod,
            TransactionReference = paymentMethod == PaymentMethod.Monnify
                ? $"PAY-{unique}"
                : paymentStatus == PaymentStatus.Paid
                    ? $"EXISTING-{unique}"
                    : null,
            PaymentProviderReference = paymentMethod == PaymentMethod.Monnify
                ? $"MNFY|TEST|{unique}"
                : null,
            StatusHistoryJson = "[]",
            CreatedAt = createdAtUtc ?? DateTime.UtcNow
        };

        db.Rooms.Add(room);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    public async Task<Room> CreateRoomAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var room = new Room
        {
            Id = Guid.NewGuid(),
            RoomNumber = $"U-{unique[..8]}",
            Name = "Update Integration Room",
            Category = RoomCategory.Standard,
            Floor = PropertyFloor.FirstFloor,
            Status = RoomStatus.Available,
            PricePerNight = 75000m,
            Capacity = 2,
            Size = "30 sqm",
            IsOnline = true,
            Description = "Room update integration test.",
            Amenities = ["Wi-Fi", "Breakfast"]
        };

        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        return room;
    }

    public async Task<Guest> LinkGuestProfileAsync(TestUser actor)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var user = await db.Users.SingleAsync(item => item.Id == actor.Id);
        if (!string.IsNullOrWhiteSpace(user.GuestId))
        {
            return await db.Guests.SingleAsync(item => item.Id == user.GuestId);
        }

        var unique = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var guest = new Guest
        {
            Id = $"GS-{unique[..16]}",
            FirstName = "Avatar",
            LastName = "Tester",
            Email = $"avatar-{unique[..12].ToLowerInvariant()}@example.test",
            Phone = "+2348000000001"
        };
        db.Guests.Add(guest);
        user.GuestId = guest.Id;
        await db.SaveChangesAsync();
        return guest;
    }

    public string GetLocalAssetPath(string publicId)
    {
        if (!publicId.StartsWith("local/", StringComparison.Ordinal))
            throw new ArgumentException("The integration fixture only resolves local image assets.", nameof(publicId));

        var relative = publicId["local/".Length..].Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(FindSolutionRoot(), "MooreHotels.WebAPI", "wwwroot", "uploads", relative);
    }

    public async Task<T> WithDbAsync<T>(Func<MooreHotelsDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        return await action(db);
    }

    private async Task<TestUser> SeedUserAsync(UserRole role)
    {
        await using var scope = Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            Name = $"Integration {role}",
            Role = role,
            Status = ProfileStatus.Active,
            EmailConfirmed = true,
            LockoutEnabled = true
        };

        var created = await userManager.CreateAsync(user, "TransferTest123!");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
        var assigned = await userManager.AddToRoleAsync(user, role.ToString());
        Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(error => error.Description)));

        var jwtService = scope.ServiceProvider.GetRequiredService<IJwtService>();
        return new TestUser(user.Id, role, jwtService.GenerateToken(user));
    }

    private void SetEnvironment(string key, string value)
    {
        _originalEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }

    private static string ReadLocalConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("MOORE_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var directory = new DirectoryInfo(FindSolutionRoot());
        var envFile = Path.Combine(directory.FullName, ".env.local");
        if (!File.Exists(envFile))
        {
            throw new InvalidOperationException(
                "Set MOORE_TEST_POSTGRES or create the ignored API .env.local file for PostgreSQL integration tests.");
        }

        const string key = "ConnectionStrings__DefaultConnection=";
        var line = File.ReadLines(envFile)
            .FirstOrDefault(value => value.StartsWith(key, StringComparison.Ordinal));
        if (line is not null)
        {
            return line[key.Length..].Trim().Trim('"');
        }

        throw new InvalidOperationException(
            "The ignored API .env.local file is required for PostgreSQL integration tests.");
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var solution = Path.Combine(directory.FullName, "MooreHotels.sln");
            if (File.Exists(solution))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the Moore Hotels solution root.");
    }

    private sealed class TestApiFactory : WebApplicationFactory<Program>
    {
        private readonly FakeMonnifyService _monnify;
        private readonly RecordingEmailService _email;

        public TestApiFactory(
            FakeMonnifyService monnify,
            RecordingEmailService email)
        {
            _monnify = monnify;
            _email = email;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Local");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMonnifyService>();
                services.AddSingleton<IMonnifyService>(_monnify);
                services.RemoveAll<IEmailService>();
                services.AddSingleton<IEmailService>(_email);
            });
        }
    }
}
