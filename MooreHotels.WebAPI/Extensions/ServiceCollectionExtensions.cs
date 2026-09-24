using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Common;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Identity;
using MooreHotels.Infrastructure.Hubs;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.Infrastructure.Repositories;
using MooreHotels.Infrastructure.Services;
using MooreHotels.WebAPI.Configuration;
using MooreHotels.WebAPI.Filters;
using MooreHotels.WebAPI.Services;

namespace MooreHotels.WebAPI.Extensions;

public static class ServiceCollectionExtensions
{
    public const string FrontendCorsPolicy = "MooreHotelsFrontends";
    public const string AuthRateLimitPolicy = "Auth";
    public const string PublicWriteRateLimitPolicy = "PublicWrite";
    public const string LookupRateLimitPolicy = "Lookup";
    public const string WebhookRateLimitPolicy = "Webhook";
    public const string ImageUploadRateLimitPolicy = "ImageUpload";
    public const string PublicReadRateLimitPolicy = "PublicRead";
    public const long DefaultRequestBodySize = 1024 * 1024;

    public static IServiceCollection AddMooreHotelsApi(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var runtime = configuration.GetSection("Runtime").Get<RuntimeSettings>() ?? new RuntimeSettings();
        var database = configuration.GetSection("Database").Get<DatabaseSettings>() ?? new DatabaseSettings();
        var jwt = configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
        var forwardedHeaders = configuration.GetSection("ForwardedHeaders")
            .Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        var monnify = configuration.GetSection("MonnifySettings")
            .Get<MonnifySettings>() ?? new MonnifySettings();
        var email = configuration.GetSection("EmailSettings")
            .Get<EmailSettings>() ?? new EmailSettings();

        services.Configure<RuntimeSettings>(configuration.GetSection("Runtime"));
        services.Configure<DatabaseSettings>(configuration.GetSection("Database"));
        services.Configure<JwtSettings>(configuration.GetSection("Jwt"));
        services.Configure<BankTransferSettings>(configuration.GetSection("BankTransferSettings"));
        services.Configure<FinancialControlsSettings>(configuration.GetSection("FinancialControls"));
        services.Configure<OperationalReadinessSettings>(configuration.GetSection("OperationalReadiness"));
        services.Configure<LaunchGateSettings>(configuration.GetSection("LaunchGate"));
        services.Configure<ProviderAcceptanceSettings>(configuration.GetSection("ProviderAcceptance"));
        services.Configure<PricingSettings>(configuration.GetSection("Pricing"));
        services.Configure<ReservationPolicySettings>(configuration.GetSection("ReservationPolicies"));
        services.Configure<ForwardedHeadersSettings>(configuration.GetSection("ForwardedHeaders"));
        services.Configure<EmailSettings>(configuration.GetSection("EmailSettings"));
        services.Configure<CloudinarySettings>(configuration.GetSection("CloudinarySettings"));
        services.Configure<R2Settings>(configuration.GetSection("R2Settings"));
        services.Configure<MonnifySettings>(configuration.GetSection("MonnifySettings"));
        services.Configure<HotelSettings>(configuration.GetSection("HotelSettings"));
        services.Configure<PrivacySettings>(configuration.GetSection("Privacy"));

        if (forwardedHeaders.Enabled)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = Math.Clamp(forwardedHeaders.ForwardLimit, 1, 3);
                // Render can append a proxy hop to X-Forwarded-For while
                // supplying a single X-Forwarded-Proto value. Requiring equal
                // list lengths would reject both headers and collapse all
                // clients onto the edge proxy address. Render edge mode is
                // still bounded to the trusted right-most hop below.
                options.RequireHeaderSymmetry = !forwardedHeaders.TrustRenderEdge;
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();

                // Render's public container port is reachable only through its
                // edge proxy. With ForwardLimit=1, ASP.NET consumes only the
                // right-most hop appended by that edge and ignores any
                // client-supplied values further left in the header chain.
                if (!forwardedHeaders.TrustRenderEdge)
                {
                    foreach (var proxy in forwardedHeaders.KnownProxies)
                    {
                        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
                    }

                    foreach (var network in forwardedHeaders.KnownNetworks)
                    {
                        var parts = network.Split('/', StringSplitOptions.TrimEntries);
                        options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                            System.Net.IPAddress.Parse(parts[0]),
                            int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
            });
        }

        services.AddControllers(options =>
            {
                options.SuppressAsyncSuffixInActionNames = false;
                options.Filters.Add<FluentValidationActionFilter>();
            })
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
                options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                options.JsonSerializerOptions.MaxDepth = 32;
            });

        services.Configure<FormOptions>(options =>
        {
            // Leave room for multipart boundaries while the validator still
            // enforces a 25 MB combined image payload.
            options.MultipartBodyLengthLimit = ImageFileValidator.MaxMultipartRequestBytes;
            options.ValueLengthLimit = 64 * 1024;
            options.ValueCountLimit = 256;
        });

        services.AddDbContextPool<MooreHotelsDbContext>(options =>
        {
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql =>
                {
                    npgsql.MigrationsAssembly("MooreHotels.Infrastructure");
                    npgsql.CommandTimeout(Math.Clamp(database.CommandTimeoutSeconds, 5, 120));
                    if (database.MaxRetryCount > 0)
                    {
                        npgsql.EnableRetryOnFailure(
                            Math.Clamp(database.MaxRetryCount, 1, 10),
                            TimeSpan.FromSeconds(5),
                            errorCodesToAdd: null);
                    }
                });

            if (environment.IsLocal())
            {
                options.EnableDetailedErrors();
            }
        }, Math.Clamp(database.ContextPoolSize, 16, 256));

        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequiredUniqueChars = 4;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            // ProtectedUserStore replaces the default EF Core user store so
            // every AspNetUserTokens value (TOTP authenticator keys, 2FA
            // recovery codes) is encrypted at rest via Data Protection
            // instead of being written as plaintext. The role store stays
            // the standard EF Core implementation.
            .AddUserStore<ProtectedUserStore>()
            .AddRoleStore<RoleStore<IdentityRole<Guid>, MooreHotelsDbContext, Guid>>()
            .AddDefaultTokenProviders();

        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromHours(2));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName($"MooreHotels.{environment.ToClientName()}");
        var keysPath = configuration["DataProtection:KeysPath"];
        if (string.Equals(configuration["DataProtection:StorageProvider"], "Database", StringComparison.OrdinalIgnoreCase))
        {
            dataProtection.PersistKeysToDbContext<MooreHotelsDbContext>();
        }
        else if (!string.IsNullOrWhiteSpace(keysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }
        var certificatePath = configuration["DataProtection:CertificatePath"];
        var certificateBase64 = configuration["DataProtection:CertificateBase64"];
        var certificatePassword = configuration["DataProtection:CertificatePassword"];
        if (!string.IsNullOrWhiteSpace(certificateBase64) &&
            !string.IsNullOrWhiteSpace(certificatePassword))
        {
            dataProtection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(certificateBase64),
                certificatePassword,
                X509KeyStorageFlags.EphemeralKeySet));
        }
        else if (!string.IsNullOrWhiteSpace(certificatePath) &&
                 !string.IsNullOrWhiteSpace(certificatePassword))
        {
            dataProtection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                certificatePassword,
                X509KeyStorageFlags.EphemeralKeySet));
        }

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = true;
                options.SaveToken = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = ClaimTypes.Role
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.HttpContext.Request.Path.StartsWithSegments("/hubs/notifications"))
                        {
                            var accessTicket = context.Request.Query["access_token"];
                            var ticketStore = context.HttpContext.RequestServices
                                .GetRequiredService<RealtimeAccessTicketStore>();
                            if (ticketStore.TryConsume(accessTicket, out var bearerToken))
                                context.Token = bearerToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            options.AddPolicy(HotelAuthorization.ReservationsRead, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(
                        context.User,
                        "Reception",
                        "FrontDesk",
                        "Concierge")));
            options.AddPolicy(HotelAuthorization.ReservationsManage, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(context.User, "Reception", "FrontDesk")));
            options.AddPolicy(HotelAuthorization.GuestPiiRead, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(context.User, "Reception", "FrontDesk")));
            options.AddPolicy(HotelAuthorization.FolioRead, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(
                        context.User,
                        "Reception",
                        "FrontDesk",
                        "Finance",
                        "Cashier")));
            options.AddPolicy(HotelAuthorization.FolioCharge, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(
                        context.User,
                        "Reception",
                        "FrontDesk",
                        "Finance",
                        "Cashier")));
            options.AddPolicy(HotelAuthorization.FolioPayment, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(context.User, "Finance", "Cashier")));
            options.AddPolicy(HotelAuthorization.FolioAdjust, policy =>
                policy.RequireRole("Admin", "Manager"));
            options.AddPolicy(HotelAuthorization.FolioClose, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(context.User, "Finance", "Cashier")));
            options.AddPolicy(HotelAuthorization.FolioIncidentals, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(
                        context.User,
                        "Reception",
                        "FrontDesk",
                        "Concierge")));
            options.AddPolicy(HotelAuthorization.OperationsRead, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") ||
                    context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(context.User, "Reception", "FrontDesk")));
            options.AddPolicy(HotelAuthorization.HousekeepingManage, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") || context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(context.User, "Housekeeping")));
            options.AddPolicy(HotelAuthorization.MaintenanceManage, policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole("Admin") || context.User.IsInRole("Manager") ||
                    HotelAuthorization.HasDepartment(context.User, "Maintenance", "Engineering")));
            options.AddPolicy(HotelAuthorization.NightAuditClose, policy =>
                policy.RequireRole("Admin", "Manager"));
            options.AddPolicy(HotelAuthorization.GuestCrmManage, policy =>
                policy.RequireRole("Admin", "Manager"));
            options.AddPolicy(HotelAuthorization.ChannelsManage, policy =>
                policy.RequireRole("Admin", "Manager"));
        });

        var origins = (configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy =>
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .SetPreflightMaxAge(TimeSpan.FromHours(1))));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers["Retry-After"] = Math.Max(
                            1,
                            (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "https://httpstatuses.com/429",
                    title = "Too Many Requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = "Please wait before trying this operation again.",
                    traceId = context.HttpContext.TraceIdentifier
                }, cancellationToken);
            };

            if (!runtime.EnableRateLimiting)
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetNoLimiter("test"));
                options.AddPolicy(AuthRateLimitPolicy, _ =>
                    RateLimitPartition.GetNoLimiter("test"));
                options.AddPolicy(PublicWriteRateLimitPolicy, _ =>
                    RateLimitPartition.GetNoLimiter("test"));
                options.AddPolicy(LookupRateLimitPolicy, _ =>
                    RateLimitPartition.GetNoLimiter("test"));
                options.AddPolicy(WebhookRateLimitPolicy, _ =>
                    RateLimitPartition.GetNoLimiter("test"));
                options.AddPolicy(ImageUploadRateLimitPolicy, _ =>
                    RateLimitPartition.GetNoLimiter("test"));
                options.AddPolicy(PublicReadRateLimitPolicy, _ =>
                    RateLimitPartition.GetNoLimiter("test"));
                return;
            }

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.Identity?.IsAuthenticated == true
                        ? $"user:{GetAuthenticatedClientKey(context)}"
                        : $"ip:{GetClientKey(context)}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(AuthRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetClientKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(PublicWriteRateLimitPolicy, context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    GetClientKey(context),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(LookupRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetClientKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(WebhookRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetClientKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(ImageUploadRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetAuthenticatedClientKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(PublicReadRateLimitPolicy, context =>
                context.User.Identity?.IsAuthenticated == true
                    ? RateLimitPartition.GetNoLimiter(GetAuthenticatedClientKey(context))
                    : RateLimitPartition.GetSlidingWindowLimiter(
                        GetClientKey(context),
                        _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = 120,
                            Window = TimeSpan.FromMinutes(1),
                            SegmentsPerWindow = 4,
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }));
        });

        services.AddResponseCompression();
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
            options.Preload = false;
        });
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Moore Hotels API",
                Version = "v1"
            });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                In = ParameterLocation.Header,
                Description = "Paste the JWT only. Swagger adds the Bearer prefix.",
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    []
                }
            });
            options.DocumentFilter<RemoveSwaggerDescriptionsFilter>();
            options.MapType<IFormFile>(() => new OpenApiSchema { Type = "string", Format = "binary" });
        });

        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = environment.IsLocal();
            options.MaximumReceiveMessageSize = 32 * 1024;
            options.MaximumParallelInvocationsPerClient = 1;
            options.StreamBufferCapacity = 10;
            options.HandshakeTimeout = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RealtimeAccessTicketStore>();
        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(http => http.ConfigureHttpClient(client =>
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(runtime.ExternalRequestTimeoutSeconds, 5, 60))));

        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IRoomRepository, RoomRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IGuestRepository, GuestRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IVisitRecordRepository, VisitRecordRepository>();
        services.AddScoped<IOperationLedgerRepository, OperationLedgerRepository>();
        services.AddScoped<IRoomService, RoomService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IFolioService, FolioService>();
        services.AddScoped<IReservationAmendmentService, ReservationAmendmentService>();
        services.AddScoped<IHousekeepingService, HousekeepingService>();
        services.AddScoped<IOperationalReportingService, OperationalReportingService>();
        services.AddScoped<IGuestCrmService, GuestCrmService>();
        services.AddScoped<IChannelManagementService, ChannelManagementService>();
        services.AddScoped<PrivacyDataService>();
        services.AddScoped<IMonnifyPaymentProcessor, MonnifyPaymentProcessor>();
        services.AddScoped<IGuestService, GuestService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IVisitRecordService, VisitRecordService>();
        services.AddScoped<IAddOnRepository, AddOnRepository>();
        services.AddScoped<IAddOnService, MooreHotels.Application.Services.AddOnService>();
        services.AddSingleton<IHotelTimeService, HotelTimeService>();
        services.AddScoped<IPdfInvoiceGenerator, PdfInvoiceGenerator>();
        services.AddScoped<IAnalyticsService, MooreHotels.Application.Services.AnalyticsService>();
        services.AddScoped<IProfileService, MooreHotels.Application.Services.ProfileService>();
        services.AddScoped<IStaffService, MooreHotels.Application.Services.StaffService>();
        services.AddScoped<IClientGuestReconciliationService, ClientGuestReconciliationService>();
        services.AddSingleton<StaffConnectionRegistry>();
        services.AddSingleton<IStaffSessionRevocationService>(provider =>
            provider.GetRequiredService<StaffConnectionRegistry>());
        services.AddScoped<IOperationService, OperationService>();
        services.AddScoped<INotificationService, MooreHotels.Infrastructure.Services.NotificationService>();
        services.AddScoped<IEmailOutbox, EmailOutbox>();
        services.AddScoped<IMediaDeletionOutbox, MediaDeletionOutbox>();
        services.AddScoped<IEmailDeliveryContext, EmailDeliveryContext>();
        services.AddScoped<IApplicationTransaction, ApplicationTransaction>();
        services.AddValidatorsFromAssemblyContaining<MooreHotels.Application.Validators.CreateBookingRequestValidator>();
        services.AddHostedService<PendingBookingExpirationWorker>();
        services.AddHostedService<EmailOutboxWorker>();
        services.AddHostedService<MediaDeletionWorker>();
        services.AddHostedService<PrivacyRetentionWorker>();
        services.AddSingleton<OrphanedMediaCleanup>();

        if (runtime.EnableExternalServices && string.Equals(
                email.DeliveryMode,
                "Brevo",
                StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(
                EmailService.HttpClientName,
                client =>
                {
                    client.BaseAddress = new Uri(
                        "https://api.brevo.com/",
                        UriKind.Absolute);
                    client.Timeout = TimeSpan.FromSeconds(
                        Math.Clamp(
                            runtime.ExternalRequestTimeoutSeconds,
                            5,
                            60));
                    client.DefaultRequestHeaders.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(
                            "application/json"));
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "MooreHotels-API/1.0");
                });
            services.AddScoped<IEmailService, EmailService>();
        }
        else
        {
            services.AddScoped<IEmailService, LocalEmailService>();
        }

        if (runtime.EnableExternalServices && monnify.Enabled)
        {
            services.AddHttpClient(
                MonnifyService.HttpClientName,
                client =>
                {
                    client.BaseAddress = new Uri(
                        $"{monnify.BaseUrl.TrimEnd('/')}/",
                        UriKind.Absolute);
                    client.Timeout = TimeSpan.FromSeconds(
                        Math.Clamp(runtime.ExternalRequestTimeoutSeconds, 5, 60));
                    client.DefaultRequestHeaders.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(
                            "application/json"));
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "MooreHotels-API/1.0");
                });
            services.AddSingleton<IMonnifyService, MonnifyService>();
        }
        else
        {
            services.AddScoped<IMonnifyService, UnavailableMonnifyService>();
        }

        if (runtime.EnableExternalServices && ConfigurationBootstrap.UsesR2Media(configuration))
        {
            services.AddScoped<IImageService, R2ImageService>();
        }
        else if (runtime.EnableExternalServices)
        {
            services.AddScoped<IImageService, CloudinaryService>();
        }
        else
        {
            services.AddScoped<IImageService, LocalImageService>();
        }

        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = DefaultRequestBodySize;
            options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            options.Limits.MaxRequestHeaderCount = 100;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });

        return services;
    }

    private static string GetClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string GetAuthenticatedClientKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? GetClientKey(context);
}
