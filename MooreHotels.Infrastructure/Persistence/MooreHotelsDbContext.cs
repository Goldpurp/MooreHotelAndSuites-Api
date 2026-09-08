using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MooreHotels.Application.Common;
using MooreHotels.Domain.Entities;
using System.Text.Json;

namespace MooreHotels.Infrastructure.Persistence;

public sealed class MooreHotelsDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IDataProtectionKeyContext
{
    public MooreHotelsDbContext(DbContextOptions<MooreHotelsDbContext> options) : base(options) { }

    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomType> RoomTypes => Set<RoomType>();
    public DbSet<ReservationRoom> ReservationRooms => Set<ReservationRoom>();
    public DbSet<RoomInventoryClosure> RoomInventoryClosures => Set<RoomInventoryClosure>();
    public DbSet<RoomInventoryPeriod> RoomInventoryPeriods => Set<RoomInventoryPeriod>();
    public DbSet<RoomImage> RoomImages => Set<RoomImage>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingEmailVerification> BookingEmailVerifications => Set<BookingEmailVerification>();
    public DbSet<BookingCodeAllocation> BookingCodeAllocations => Set<BookingCodeAllocation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<VisitRecord> VisitRecords => Set<VisitRecord>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<MonnifyTransaction> MonnifyTransactions => Set<MonnifyTransaction>();
    public DbSet<EmailOutboxMessage> EmailOutboxMessages => Set<EmailOutboxMessage>();
    public DbSet<AddOnService> AddOnServices => Set<AddOnService>();
    public DbSet<BookingAddOn> BookingAddOns => Set<BookingAddOn>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<NotificationReceipt> NotificationReceipts => Set<NotificationReceipt>();
    public DbSet<MediaDeletionJob> MediaDeletionJobs => Set<MediaDeletionJob>();
    public DbSet<PrivacyRequest> PrivacyRequests => Set<PrivacyRequest>();
    public DbSet<RatePlan> RatePlans => Set<RatePlan>();
    public DbSet<DailyRoomRate> DailyRoomRates => Set<DailyRoomRate>();
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<BookingQuote> BookingQuotes => Set<BookingQuote>();
    public DbSet<BookingQuoteLine> BookingQuoteLines => Set<BookingQuoteLine>();
    public DbSet<Folio> Folios => Set<Folio>();
    public DbSet<FolioEntry> FolioEntries => Set<FolioEntry>();
    public DbSet<BookingAmendment> BookingAmendments => Set<BookingAmendment>();
    public DbSet<HousekeepingTask> HousekeepingTasks => Set<HousekeepingTask>();
    public DbSet<MaintenanceWorkOrder> MaintenanceWorkOrders => Set<MaintenanceWorkOrder>();
    public DbSet<NightAudit> NightAudits => Set<NightAudit>();
    public DbSet<GuestNote> GuestNotes => Set<GuestNote>();
    public DbSet<GuestMerge> GuestMerges => Set<GuestMerge>();
    public DbSet<DistributionChannel> DistributionChannels => Set<DistributionChannel>();
    public DbSet<ChannelEvent> ChannelEvents => Set<ChannelEvent>();
    public DbSet<ChannelReservationMapping> ChannelReservationMappings => Set<ChannelReservationMapping>();
    public DbSet<DatabaseEnvironmentBoundary> DatabaseEnvironmentBoundaries =>
        Set<DatabaseEnvironmentBoundary>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareChanges()
    {
        EnforceImmutableFolioEntries();
        foreach (var entry in ChangeTracker.Entries<AuditLog>()
                     .Where(entry => entry.State == EntityState.Added))
        {
            entry.Entity.OldDataJson = AuditDataSanitizer.SanitizeJson(entry.Entity.OldDataJson);
            entry.Entity.NewDataJson = AuditDataSanitizer.SanitizeJson(entry.Entity.NewDataJson);
        }
    }

    private void EnforceImmutableFolioEntries()
    {
        if (ChangeTracker.Entries<DatabaseEnvironmentBoundary>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "The database environment boundary is immutable.");
        }
        if (ChangeTracker.Entries<FolioEntry>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Folio entries are immutable. Post a void or reversing entry instead.");
        }
        if (ChangeTracker.Entries<BookingAmendment>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<GuestMerge>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<NightAudit>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Operational history is append-only and cannot be edited or deleted.");
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        var listConverter = new ValueConverter<List<string>, string>(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());
        var listComparer = new ValueComparer<List<string>>(
            (left, right) => ReferenceEquals(left, right) ||
                             left != null && right != null && left.SequenceEqual(right),
            value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
            value => value.ToList());

        builder.Entity<DatabaseEnvironmentBoundary>(entity =>
        {
            entity.ToTable("environment_boundaries", table =>
            {
                table.HasCheckConstraint(
                    "CK_environment_boundaries_singleton",
                    "\"Id\" = 1");
                table.HasCheckConstraint(
                    "CK_environment_boundaries_environment",
                    "\"EnvironmentName\" IN ('local', 'production')");
            });
            entity.HasKey(boundary => boundary.Id);
            entity.Property(boundary => boundary.EnvironmentName)
                .HasMaxLength(16)
                .IsRequired();
        });

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.ToTable("users", table =>
            {
                table.HasCheckConstraint(
                    "CK_users_privacy_acceptance_consistent",
                    "(\"PrivacyPolicyVersion\" IS NULL AND \"PrivacyPolicyAcceptedAtUtc\" IS NULL) OR (\"PrivacyPolicyVersion\" IS NOT NULL AND \"PrivacyPolicyAcceptedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_users_anonymized_accounts_suspended",
                    "\"AnonymizedAtUtc\" IS NULL OR \"Status\" = 'Suspended'");
            });
            entity.Property(user => user.Name).HasMaxLength(160).IsRequired();
            entity.Property(user => user.AvatarUrl).HasMaxLength(2048);
            entity.Property(user => user.AvatarPublicId).HasMaxLength(512);
            entity.Property(user => user.Department).HasMaxLength(80);
            entity.Property(user => user.GuestId).HasMaxLength(20);
            entity.Property(user => user.PrivacyPolicyVersion).HasMaxLength(80);
            entity.Property(user => user.Role).HasConversion<string>().HasMaxLength(30);
            entity.Property(user => user.Status).HasConversion<string>().HasMaxLength(30);
            entity.HasIndex(user => new { user.Status, user.Role });
            entity.HasIndex(user => new
            {
                user.Role,
                user.AnonymizedAtUtc,
                user.LastAuthenticatedAtUtc,
                user.StatusChangedAtUtc
            });
            entity.HasIndex(user => user.GuestId).IsUnique()
                .HasFilter("\"GuestId\" IS NOT NULL");
            entity.HasOne(user => user.GuestProfile)
                .WithMany()
                .HasForeignKey(user => user.GuestId)
                .OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<IdentityRole<Guid>>(entity => entity.ToTable("roles"));
        builder.Entity<IdentityUserRole<Guid>>(entity => entity.ToTable("user_roles"));

        builder.Entity<RoomType>(entity =>
        {
            entity.ToTable("room_types", table =>
            {
                table.HasCheckConstraint(
                    "CK_room_types_occupancy",
                    "\"BaseOccupancy\" >= 1 AND \"MaxOccupancy\" >= \"BaseOccupancy\" AND \"MaxOccupancy\" <= 50");
                table.HasCheckConstraint(
                    "CK_room_types_base_price_positive",
                    "\"BasePricePerNight\" > 0");
            });
            entity.HasKey(type => type.Id);
            entity.Property(type => type.Code).HasMaxLength(30).IsRequired();
            entity.Property(type => type.Name).HasMaxLength(120).IsRequired();
            entity.Property(type => type.Category).HasConversion<string>().HasMaxLength(40);
            entity.Property(type => type.BasePricePerNight).HasPrecision(18, 2);
            entity.Property(type => type.Description).HasMaxLength(2000).IsRequired();
            entity.Property(type => type.Amenities)
                .HasColumnType("jsonb")
                .HasConversion(listConverter, listComparer);
            entity.HasIndex(type => type.Code).IsUnique();
            entity.HasIndex(type => new { type.IsActive, type.Category, type.MaxOccupancy });
        });

        builder.Entity<Room>(entity =>
        {
            entity.ToTable("rooms", table =>
            {
                table.HasCheckConstraint("CK_rooms_capacity_positive", "\"Capacity\" > 0");
                table.HasCheckConstraint("CK_rooms_price_positive", "\"PricePerNight\" > 0");
            });
            entity.HasIndex(room => room.RoomNumber).IsUnique();
            entity.HasIndex(room => new { room.IsOnline, room.Category, room.Capacity });
            entity.Property(room => room.RoomNumber).HasMaxLength(30).IsRequired();
            entity.Property(room => room.Name).HasMaxLength(120).IsRequired();
            entity.Property(room => room.Category).HasConversion<string>().HasMaxLength(40);
            entity.Property(room => room.Floor).HasConversion<string>().HasMaxLength(40);
            entity.Property(room => room.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(room => room.Size).HasMaxLength(50).IsRequired();
            entity.Property(room => room.Description).HasMaxLength(4000).IsRequired();
            entity.Property(room => room.PricePerNight).HasPrecision(18, 2);
            entity.Property(room => room.Amenities)
                .HasColumnType("jsonb")
                .HasConversion(listConverter, listComparer);
            entity.HasMany(room => room.Images)
                .WithOne(image => image.Room)
                .HasForeignKey(image => image.RoomId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(room => room.RoomType)
                .WithMany(type => type.Rooms)
                .HasForeignKey(room => room.RoomTypeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(room => room.RoomTypeId);
        });

        builder.Entity<RoomImage>(entity =>
        {
            entity.ToTable("room_images");
            entity.HasKey(image => image.Id);
            entity.Property(image => image.Url).HasMaxLength(2048).IsRequired();
            entity.Property(image => image.PublicId).HasMaxLength(512).IsRequired();
            entity.HasIndex(image => image.PublicId).IsUnique();
        });

        builder.Entity<RoomInventoryPeriod>(entity =>
        {
            entity.ToTable("room_inventory_periods", table => table.HasCheckConstraint(
                "CK_room_inventory_periods_dates",
                "\"EndDate\" IS NULL OR \"EndDate\" > \"StartDate\""));
            entity.HasKey(period => period.Id);
            entity.HasIndex(period => new { period.RoomId, period.StartDate, period.EndDate });
            entity.HasIndex(period => period.RoomId).IsUnique()
                .HasFilter("\"EndDate\" IS NULL");
            entity.HasOne(period => period.Room)
                .WithMany(room => room.InventoryPeriods)
                .HasForeignKey(period => period.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MediaAsset>(entity =>
        {
            entity.ToTable("media_assets");
            entity.HasKey(asset => asset.Id);
            entity.Property(asset => asset.Url).HasMaxLength(2048).IsRequired();
            entity.Property(asset => asset.PublicId).HasMaxLength(512).IsRequired();
            entity.Property(asset => asset.Folder).HasMaxLength(80).IsRequired();
            entity.HasIndex(asset => asset.PublicId).IsUnique();
            entity.HasIndex(asset => new { asset.Folder, asset.CreatedAtUtc });
            entity.HasIndex(asset => asset.UploadedByUserId);
            entity.HasOne(asset => asset.UploadedByUser)
                .WithMany()
                .HasForeignKey(asset => asset.UploadedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<MediaDeletionJob>(entity =>
        {
            entity.ToTable("media_deletion_outbox", table => table.HasCheckConstraint(
                "CK_media_deletion_outbox_attempt_count",
                "\"AttemptCount\" >= 0"));
            entity.HasKey(job => job.Id);
            entity.Property(job => job.PublicId).HasMaxLength(512).IsRequired();
            entity.Property(job => job.SourceType).HasMaxLength(50).IsRequired();
            entity.Property(job => job.SourceId).HasMaxLength(160).IsRequired();
            entity.Property(job => job.LastErrorCode).HasMaxLength(80);
            entity.HasIndex(job => job.PublicId).IsUnique();
            entity.HasIndex(job => new
            {
                job.NextAttemptAtUtc,
                job.LockedUntilUtc,
                job.AttemptCount
            });
            entity.HasIndex(job => job.CreatedAtUtc);
        });

        builder.Entity<Guest>(entity =>
        {
            entity.ToTable("guests", table => table.HasCheckConstraint(
                "CK_guests_merge_state",
                "(\"MergedIntoGuestId\" IS NULL AND \"MergedAtUtc\" IS NULL AND \"MergedByUserId\" IS NULL) OR (\"MergedIntoGuestId\" IS NOT NULL AND \"MergedAtUtc\" IS NOT NULL AND \"MergedByUserId\" IS NOT NULL AND \"MergedIntoGuestId\" <> \"Id\")"));
            entity.HasKey(guest => guest.Id);
            entity.Property(guest => guest.Id).HasMaxLength(20);
            entity.Property(guest => guest.FirstName).HasMaxLength(80).IsRequired();
            entity.Property(guest => guest.LastName).HasMaxLength(80).IsRequired();
            entity.Property(guest => guest.Email).HasMaxLength(254).IsRequired();
            entity.Property(guest => guest.Phone).HasMaxLength(30).IsRequired();
            entity.Property(guest => guest.NormalizedEmail).HasMaxLength(254).IsRequired();
            entity.Property(guest => guest.NormalizedPhone).HasMaxLength(30).IsRequired();
            entity.Property(guest => guest.PreferencesJson).HasColumnType("jsonb");
            entity.Property(guest => guest.AvatarUrl).HasMaxLength(2048);
            entity.Property(guest => guest.LegalHoldReason).HasMaxLength(500);
            entity.HasIndex(guest => guest.Email);
            entity.HasIndex(guest => new { guest.Email, guest.FirstName, guest.LastName });
            entity.HasIndex(guest => new { guest.NormalizedEmail, guest.MergedIntoGuestId });
            entity.HasIndex(guest => new { guest.NormalizedPhone, guest.MergedIntoGuestId });
            entity.HasIndex(guest => guest.AnonymizedAtUtc);
            entity.HasIndex(guest => guest.IsUnderLegalHold);
            entity.HasOne(guest => guest.MergedIntoGuest)
                .WithMany()
                .HasForeignKey(guest => guest.MergedIntoGuestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(guest => guest.MergedByUser)
                .WithMany()
                .HasForeignKey(guest => guest.MergedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Booking>(entity =>
        {
            entity.ToTable("bookings", table =>
            {
                table.HasCheckConstraint(
                    "CK_bookings_valid_dates",
                    "\"CheckOut\" > \"CheckIn\"");
                table.HasCheckConstraint(
                    "CK_bookings_occupancy_counts",
                    "\"AdultCount\" >= 1 AND \"ChildCount\" >= 0");
                table.HasCheckConstraint(
                    "CK_bookings_room_quantity",
                    "\"RoomQuantity\" BETWEEN 1 AND 10");
                table.HasCheckConstraint(
                    "CK_bookings_policy_acceptance_consistent",
                    "(\"PrivacyPolicyVersion\" IS NULL AND \"BookingTermsVersion\" IS NULL AND \"PoliciesAcceptedAtUtc\" IS NULL) OR (\"PrivacyPolicyVersion\" IS NOT NULL AND \"BookingTermsVersion\" IS NOT NULL AND \"PoliciesAcceptedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_bookings_guest_access_window",
                    "\"GuestAccessTokenExpiresAtUtc\" IS NULL OR (\"GuestAccessTokenIssuedAtUtc\" IS NOT NULL AND \"GuestAccessTokenExpiresAtUtc\" > \"GuestAccessTokenIssuedAtUtc\")");
                table.HasCheckConstraint(
                    "CK_bookings_price_breakdown",
                    "\"Amount\" >= 0 AND \"RoomSubtotal\" >= 0 AND \"DiscountAmount\" >= 0 AND \"IncludedTaxAmount\" >= 0 AND \"TaxAmount\" >= 0 AND \"FeeAmount\" >= 0");
                table.HasCheckConstraint(
                    "CK_bookings_currency",
                    "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "CK_bookings_reservation_policy",
                    "\"FreeCancellationHours\" BETWEEN 0 AND 720 AND \"CancellationPenaltyPercent\" BETWEEN 0 AND 100 AND \"DepositPercent\" BETWEEN 0 AND 100 AND \"NoShowPenaltyPercent\" BETWEEN 0 AND 100 AND \"CancellationPenaltyAmount\" >= 0 AND \"NoShowPenaltyAmount\" >= 0");
            });
            entity.HasIndex(booking => booking.BookingCode).IsUnique();
            entity.HasIndex(booking => booking.TransactionReference).IsUnique()
                .HasFilter("\"TransactionReference\" IS NOT NULL");
            entity.HasIndex(booking => booking.PaymentProviderReference).IsUnique()
                .HasFilter("\"PaymentProviderReference\" IS NOT NULL");
            entity.HasIndex(booking => booking.RefundReference).IsUnique()
                .HasFilter("\"RefundReference\" IS NOT NULL");
            entity.HasIndex(booking => new { booking.RoomId, booking.CheckIn, booking.CheckOut, booking.Status });
            entity.HasIndex(booking => new { booking.GuestId, booking.CreatedAt });
            entity.HasIndex(booking => new { booking.PaymentStatus, booking.CreatedAt });
            entity.HasIndex(booking => new { booking.CreatedAt, booking.Id });
            entity.HasIndex(booking => new { booking.CancelledAtUtc, booking.Id })
                .HasFilter("\"Status\" = 'Cancelled' AND \"CancelledAtUtc\" IS NOT NULL");
            entity.HasIndex(booking => new
            {
                booking.GuestAccessTokenExpiresAtUtc,
                booking.Id
            }).HasFilter("\"GuestAccessTokenRevokedAtUtc\" IS NULL AND \"GuestAccessTokenExpiresAtUtc\" IS NOT NULL");
            entity.Property(booking => booking.BookingCode).HasMaxLength(30).IsRequired();
            entity.Property(booking => booking.AdultCount).HasDefaultValue(1);
            entity.Property(booking => booking.RoomQuantity).HasDefaultValue(1);
            entity.Property(booking => booking.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.PaymentStatus).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.PaymentMethod).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.TransactionReference).HasMaxLength(160);
            entity.Property(booking => booking.PaymentProviderReference).HasMaxLength(160);
            entity.Property(booking => booking.PaymentCheckoutUrl).HasMaxLength(2048);
            entity.Property(booking => booking.PaymentConfirmationMethod).HasMaxLength(50);
            entity.Property(booking => booking.RefundReference).HasMaxLength(160);
            entity.Property(booking => booking.RefundAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.RefundApprovedAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.RefundChannel).HasMaxLength(40);
            entity.Property(booking => booking.RefundEvidenceType).HasMaxLength(40);
            entity.Property(booking => booking.RefundNotes).HasMaxLength(500);
            entity.Property(booking => booking.Notes).HasMaxLength(1000);
            entity.Property(booking => booking.Currency).HasMaxLength(3).IsRequired()
                .HasDefaultValue("NGN");
            entity.Property(booking => booking.RoomSubtotal).HasPrecision(18, 2);
            entity.Property(booking => booking.DiscountAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.IncludedTaxAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.TaxAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.FeeAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.Amount).HasPrecision(18, 2);
            entity.Property(booking => booking.ReservationPolicyVersion).HasMaxLength(80).IsRequired();
            entity.Property(booking => booking.CancellationPenaltyPercent).HasPrecision(5, 2);
            entity.Property(booking => booking.DepositPercent).HasPrecision(5, 2);
            entity.Property(booking => booking.NoShowPenaltyPercent).HasPrecision(5, 2);
            entity.Property(booking => booking.CancellationPenaltyAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.NoShowPenaltyAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.StatusHistoryJson).HasColumnType("jsonb");
            entity.Property(booking => booking.GuestAccessTokenHash).HasMaxLength(44);
            entity.Property(booking => booking.PrivacyPolicyVersion).HasMaxLength(80);
            entity.Property(booking => booking.BookingTermsVersion).HasMaxLength(80);
            entity.HasOne(booking => booking.Room)
                .WithMany()
                .HasForeignKey(booking => booking.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(booking => booking.RoomType)
                .WithMany()
                .HasForeignKey(booking => booking.RoomTypeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(booking => new { booking.RoomTypeId, booking.CheckIn, booking.CheckOut, booking.Status });
            entity.HasOne(booking => booking.Guest)
                .WithMany(guest => guest.Bookings)
                .HasForeignKey(booking => booking.GuestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(booking => booking.Quote)
                .WithOne(quote => quote.Booking)
                .HasForeignKey<Booking>(booking => booking.QuoteId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(booking => booking.QuoteId).IsUnique()
                .HasFilter("\"QuoteId\" IS NOT NULL");
            entity.HasOne(booking => booking.PaymentConfirmedByUser)
                .WithMany()
                .HasForeignKey(booking => booking.PaymentConfirmedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(booking => booking.PaymentConfirmedByUserId);
            entity.HasOne(booking => booking.RefundApprovedByUser)
                .WithMany()
                .HasForeignKey(booking => booking.RefundApprovedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(booking => booking.RefundProcessedByUser)
                .WithMany()
                .HasForeignKey(booking => booking.RefundProcessedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(booking => booking.RefundApprovedByUserId);
            entity.HasIndex(booking => booking.RefundProcessedByUserId);
        });

        builder.Entity<ReservationRoom>(entity =>
        {
            entity.ToTable("reservation_rooms", table => table.HasCheckConstraint(
                "CK_reservation_rooms_sequence",
                "\"Sequence\" BETWEEN 1 AND 10"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.RoomTypeCode).HasMaxLength(30).IsRequired();
            entity.Property(item => item.RoomTypeName).HasMaxLength(120).IsRequired();
            entity.HasIndex(item => new { item.BookingId, item.Sequence }).IsUnique();
            entity.HasIndex(item => new { item.RoomTypeId, item.BookingId });
            entity.HasIndex(item => new { item.AssignedRoomId, item.BookingId })
                .HasFilter("\"AssignedRoomId\" IS NOT NULL");
            entity.HasOne(item => item.Booking)
                .WithMany(booking => booking.ReservationRooms)
                .HasForeignKey(item => item.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.RoomType)
                .WithMany(type => type.ReservationRooms)
                .HasForeignKey(item => item.RoomTypeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.AssignedRoom)
                .WithMany(room => room.Assignments)
                .HasForeignKey(item => item.AssignedRoomId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.AssignedByUser)
                .WithMany()
                .HasForeignKey(item => item.AssignedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RoomInventoryClosure>(entity =>
        {
            entity.ToTable("room_inventory_closures", table =>
            {
                table.HasCheckConstraint(
                    "CK_room_inventory_closures_dates",
                    "\"EndDate\" > \"StartDate\"");
                table.HasCheckConstraint(
                    "CK_room_inventory_closures_units",
                    "\"Units\" > 0 AND (\"RoomId\" IS NULL OR \"Units\" = 1)");
            });
            entity.HasKey(closure => closure.Id);
            entity.Property(closure => closure.Reason).HasMaxLength(500).IsRequired();
            entity.HasIndex(closure => new
            {
                closure.RoomTypeId,
                closure.StartDate,
                closure.EndDate,
                closure.IsActive
            });
            entity.HasIndex(closure => new { closure.RoomId, closure.StartDate, closure.EndDate })
                .HasFilter("\"RoomId\" IS NOT NULL");
            entity.HasOne(closure => closure.RoomType)
                .WithMany()
                .HasForeignKey(closure => closure.RoomTypeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(closure => closure.Room)
                .WithMany()
                .HasForeignKey(closure => closure.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(closure => closure.CreatedByUser)
                .WithMany()
                .HasForeignKey(closure => closure.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RatePlan>(entity =>
        {
            entity.ToTable("rate_plans", table =>
            {
                table.HasCheckConstraint(
                    "CK_rate_plans_nights",
                    "\"MinimumNights\" >= 1 AND \"MaximumNights\" >= \"MinimumNights\" AND \"MaximumNights\" <= 90");
                table.HasCheckConstraint(
                    "CK_rate_plans_adjustment",
                    "\"BaseAdjustmentValue\" >= 0 AND (\"BaseAdjustmentType\" <> 'None' OR \"BaseAdjustmentValue\" = 0)");
                table.HasCheckConstraint(
                    "CK_rate_plans_sell_dates",
                    "\"SellUntilDate\" IS NULL OR \"SellFromDate\" IS NULL OR \"SellUntilDate\" >= \"SellFromDate\"");
                table.HasCheckConstraint("CK_rate_plans_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(plan => plan.Id);
            entity.Property(plan => plan.Code).HasMaxLength(30).IsRequired();
            entity.Property(plan => plan.Name).HasMaxLength(120).IsRequired();
            entity.Property(plan => plan.Description).HasMaxLength(1000);
            entity.Property(plan => plan.Currency).HasMaxLength(3).IsRequired();
            entity.Property(plan => plan.BaseAdjustmentType).HasConversion<string>().HasMaxLength(30);
            entity.Property(plan => plan.BaseAdjustmentValue).HasPrecision(18, 4);
            entity.HasIndex(plan => plan.Code).IsUnique();
            entity.HasIndex(plan => plan.IsDefault).IsUnique()
                .HasFilter("\"IsDefault\" = TRUE AND \"IsActive\" = TRUE");
            entity.HasIndex(plan => new { plan.IsActive, plan.SellFromDate, plan.SellUntilDate });
        });

        builder.Entity<DailyRoomRate>(entity =>
        {
            entity.ToTable("daily_room_rates", table =>
            {
                table.HasCheckConstraint("CK_daily_room_rates_amount", "\"Amount\" > 0");
                table.HasCheckConstraint(
                    "CK_daily_room_rates_scope",
                    "(CASE WHEN \"RoomId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"RoomTypeId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"RoomCategory\" IS NULL THEN 0 ELSE 1 END) = 1");
            });
            entity.HasKey(rate => rate.Id);
            entity.Property(rate => rate.RoomCategory).HasConversion<string>().HasMaxLength(40);
            entity.Property(rate => rate.Amount).HasPrecision(18, 2);
            entity.HasOne(rate => rate.RatePlan)
                .WithMany(plan => plan.DailyRates)
                .HasForeignKey(rate => rate.RatePlanId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(rate => rate.Room)
                .WithMany()
                .HasForeignKey(rate => rate.RoomId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(rate => rate.RoomType)
                .WithMany()
                .HasForeignKey(rate => rate.RoomTypeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(rate => new { rate.RatePlanId, rate.RoomId, rate.StayDate })
                .IsUnique()
                .HasFilter("\"RoomId\" IS NOT NULL");
            entity.HasIndex(rate => new { rate.RatePlanId, rate.RoomCategory, rate.StayDate })
                .IsUnique()
                .HasFilter("\"RoomCategory\" IS NOT NULL");
            entity.HasIndex(rate => new { rate.RatePlanId, rate.RoomTypeId, rate.StayDate })
                .IsUnique()
                .HasFilter("\"RoomTypeId\" IS NOT NULL");
        });

        builder.Entity<PricingRule>(entity =>
        {
            entity.ToTable("pricing_rules", table =>
            {
                table.HasCheckConstraint("CK_pricing_rules_value", "\"Value\" > 0");
                table.HasCheckConstraint(
                    "CK_pricing_rules_dates",
                    "\"EffectiveUntilDate\" IS NULL OR \"EffectiveFromDate\" IS NULL OR \"EffectiveUntilDate\" >= \"EffectiveFromDate\"");
                table.HasCheckConstraint(
                    "CK_pricing_rules_inclusive",
                    "NOT \"IsInclusive\" OR (\"Kind\" = 'Tax' AND \"Calculation\" = 'Percentage')");
                table.HasCheckConstraint("CK_pricing_rules_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(rule => rule.Id);
            entity.Property(rule => rule.Code).HasMaxLength(30).IsRequired();
            entity.Property(rule => rule.Name).HasMaxLength(120).IsRequired();
            entity.Property(rule => rule.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(rule => rule.Calculation).HasConversion<string>().HasMaxLength(30);
            entity.Property(rule => rule.Value).HasPrecision(18, 4);
            entity.Property(rule => rule.Currency).HasMaxLength(3).IsRequired();
            entity.HasIndex(rule => rule.Code).IsUnique();
            entity.HasIndex(rule => new { rule.IsActive, rule.SortOrder });
        });

        builder.Entity<Promotion>(entity =>
        {
            entity.ToTable("promotions", table =>
            {
                table.HasCheckConstraint("CK_promotions_value", "\"Value\" > 0");
                table.HasCheckConstraint("CK_promotions_window", "\"ValidUntilUtc\" > \"ValidFromUtc\"");
                table.HasCheckConstraint("CK_promotions_redemptions", "\"RedemptionCount\" >= 0 AND (\"RedemptionLimit\" IS NULL OR \"RedemptionLimit\" > 0)");
                table.HasCheckConstraint("CK_promotions_minimum_nights", "\"MinimumNights\" BETWEEN 1 AND 90");
                table.HasCheckConstraint("CK_promotions_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "CK_promotions_percentage",
                    "\"DiscountType\" <> 'Percentage' OR \"Value\" <= 100");
            });
            entity.HasKey(promotion => promotion.Id);
            entity.Property(promotion => promotion.Code).HasMaxLength(40).IsRequired();
            entity.Property(promotion => promotion.Name).HasMaxLength(120).IsRequired();
            entity.Property(promotion => promotion.DiscountType).HasConversion<string>().HasMaxLength(30);
            entity.Property(promotion => promotion.Value).HasPrecision(18, 4);
            entity.Property(promotion => promotion.MaximumDiscountAmount).HasPrecision(18, 2);
            entity.Property(promotion => promotion.Currency).HasMaxLength(3).IsRequired();
            entity.HasIndex(promotion => promotion.Code).IsUnique();
            entity.HasIndex(promotion => new { promotion.IsActive, promotion.ValidFromUtc, promotion.ValidUntilUtc });
            entity.HasOne(promotion => promotion.RatePlan)
                .WithMany()
                .HasForeignKey(promotion => promotion.RatePlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BookingQuote>(entity =>
        {
            entity.ToTable("booking_quotes", table =>
            {
                table.HasCheckConstraint("CK_booking_quotes_window", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                table.HasCheckConstraint("CK_booking_quotes_dates", "\"CheckOutDate\" > \"CheckInDate\"");
                table.HasCheckConstraint("CK_booking_quotes_occupancy", "\"AdultCount\" >= 1 AND \"ChildCount\" >= 0");
                table.HasCheckConstraint("CK_booking_quotes_room_quantity", "\"RoomQuantity\" BETWEEN 1 AND 10");
                table.HasCheckConstraint(
                    "CK_booking_quotes_totals",
                    "\"RoomSubtotal\" >= 0 AND \"DiscountAmount\" BETWEEN 0 AND \"RoomSubtotal\" AND \"IncludedTaxAmount\" >= 0 AND \"TaxAmount\" >= 0 AND \"FeeAmount\" >= 0 AND \"TotalAmount\" = \"RoomSubtotal\" - \"DiscountAmount\" + \"TaxAmount\" + \"FeeAmount\"");
                table.HasCheckConstraint("CK_booking_quotes_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(quote => quote.Id);
            entity.Property(quote => quote.AccessTokenHash).HasMaxLength(44).IsRequired();
            entity.Property(quote => quote.Currency).HasMaxLength(3).IsRequired();
            entity.Property(quote => quote.RoomSubtotal).HasPrecision(18, 2);
            entity.Property(quote => quote.DiscountAmount).HasPrecision(18, 2);
            entity.Property(quote => quote.IncludedTaxAmount).HasPrecision(18, 2);
            entity.Property(quote => quote.TaxAmount).HasPrecision(18, 2);
            entity.Property(quote => quote.FeeAmount).HasPrecision(18, 2);
            entity.Property(quote => quote.TotalAmount).HasPrecision(18, 2);
            entity.HasIndex(quote => quote.AccessTokenHash).IsUnique();
            entity.HasIndex(quote => new { quote.ExpiresAtUtc, quote.ConsumedAtUtc });
            entity.HasIndex(quote => quote.AmendmentBookingId);
            entity.HasOne(quote => quote.Room).WithMany().HasForeignKey(quote => quote.RoomId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(quote => quote.AmendmentBooking).WithMany()
                .HasForeignKey(quote => quote.AmendmentBookingId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(quote => quote.RoomType).WithMany().HasForeignKey(quote => quote.RoomTypeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(quote => quote.RatePlan).WithMany().HasForeignKey(quote => quote.RatePlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(quote => quote.Promotion).WithMany().HasForeignKey(quote => quote.PromotionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(quote => quote.Lines)
                .WithOne(line => line.BookingQuote)
                .HasForeignKey(line => line.BookingQuoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BookingQuoteLine>(entity =>
        {
            entity.ToTable("booking_quote_lines", table =>
            {
                table.HasCheckConstraint("CK_booking_quote_lines_quantity", "\"Quantity\" > 0");
                table.HasCheckConstraint("CK_booking_quote_lines_amount", "\"Amount\" >= 0");
                table.HasCheckConstraint(
                    "CK_booking_quote_lines_stay_date",
                    "(\"Type\" = 'RoomNight' AND \"StayDate\" IS NOT NULL) OR (\"Type\" <> 'RoomNight' AND \"StayDate\" IS NULL)");
            });
            entity.HasKey(line => line.Id);
            entity.Property(line => line.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(line => line.Code).HasMaxLength(40).IsRequired();
            entity.Property(line => line.Description).HasMaxLength(200).IsRequired();
            entity.Property(line => line.UnitAmount).HasPrecision(18, 2);
            entity.Property(line => line.Amount).HasPrecision(18, 2);
            entity.HasIndex(line => new { line.BookingQuoteId, line.SortOrder, line.Id });
        });

        builder.Entity<Folio>(entity =>
        {
            entity.ToTable("folios", table =>
            {
                table.HasCheckConstraint("CK_folios_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "CK_folios_closed_state",
                    "(\"Status\" = 'Open' AND \"ClosedAtUtc\" IS NULL AND \"ClosedByUserId\" IS NULL) OR (\"Status\" = 'Closed' AND \"ClosedAtUtc\" IS NOT NULL AND \"ClosedByUserId\" IS NOT NULL)");
            });
            entity.HasKey(folio => folio.Id);
            entity.Property(folio => folio.Currency).HasMaxLength(3).IsRequired();
            entity.Property(folio => folio.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(folio => folio.BookingId).IsUnique();
            entity.HasOne(folio => folio.Booking)
                .WithOne(booking => booking.Folio)
                .HasForeignKey<Folio>(folio => folio.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(folio => folio.ClosedByUser)
                .WithMany()
                .HasForeignKey(folio => folio.ClosedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<FolioEntry>(entity =>
        {
            entity.ToTable("folio_entries", table =>
            {
                table.HasCheckConstraint("CK_folio_entries_amount", "\"Amount\" > 0");
                table.HasCheckConstraint("CK_folio_entries_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint(
                    "CK_folio_entries_direction",
                    "(\"Type\" IN ('RoomCharge','AddOnCharge','Tax','Fee','Refund') AND \"Direction\" = 'Debit') OR (\"Type\" IN ('Discount','Payment','Credit') AND \"Direction\" = 'Credit') OR \"Type\" IN ('Adjustment','Void')");
                table.HasCheckConstraint(
                    "CK_folio_entries_void_reference",
                    "(\"Type\" = 'Void' AND \"ReversesEntryId\" IS NOT NULL) OR (\"Type\" <> 'Void' AND \"ReversesEntryId\" IS NULL)");
            });
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(entry => entry.Direction).HasConversion<string>().HasMaxLength(10);
            entity.Property(entry => entry.Amount).HasPrecision(18, 2);
            entity.Property(entry => entry.Currency).HasMaxLength(3).IsRequired();
            entity.Property(entry => entry.Description).HasMaxLength(200).IsRequired();
            entity.Property(entry => entry.SourceType).HasMaxLength(80).IsRequired();
            entity.Property(entry => entry.SourceId).HasMaxLength(160);
            entity.Property(entry => entry.ExternalReference).HasMaxLength(160);
            entity.Property(entry => entry.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(entry => entry.Notes).HasMaxLength(500);
            entity.HasIndex(entry => entry.IdempotencyKey).IsUnique();
            entity.HasIndex(entry => entry.ExternalReference).IsUnique()
                .HasFilter("\"ExternalReference\" IS NOT NULL");
            entity.HasIndex(entry => entry.ReversesEntryId).IsUnique()
                .HasFilter("\"ReversesEntryId\" IS NOT NULL");
            entity.HasIndex(entry => new { entry.FolioId, entry.PostedAtUtc, entry.Id });
            entity.HasOne(entry => entry.Folio)
                .WithMany(folio => folio.Entries)
                .HasForeignKey(entry => entry.FolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.ReversesEntry)
                .WithOne()
                .HasForeignKey<FolioEntry>(entry => entry.ReversesEntryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.PostedByUser)
                .WithMany()
                .HasForeignKey(entry => entry.PostedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<BookingAmendment>(entity =>
        {
            entity.ToTable("booking_amendments", table =>
            {
                table.HasCheckConstraint("CK_booking_amendments_dates", "\"CheckOut\" > \"CheckIn\"");
                table.HasCheckConstraint("CK_booking_amendments_occupancy", "\"AdultCount\" >= 1 AND \"ChildCount\" >= 0");
                table.HasCheckConstraint("CK_booking_amendments_room_quantity", "\"RoomQuantity\" BETWEEN 1 AND 10");
                table.HasCheckConstraint("CK_booking_amendments_amounts", "\"PreviousAmount\" >= 0 AND \"NewAmount\" >= 0 AND \"PriceDifference\" = \"NewAmount\" - \"PreviousAmount\"");
            });
            entity.HasKey(amendment => amendment.Id);
            entity.Property(amendment => amendment.PreviousStateJson).HasColumnType("jsonb");
            entity.Property(amendment => amendment.NewStateJson).HasColumnType("jsonb");
            entity.Property(amendment => amendment.PreviousAmount).HasPrecision(18, 2);
            entity.Property(amendment => amendment.NewAmount).HasPrecision(18, 2);
            entity.Property(amendment => amendment.PriceDifference).HasPrecision(18, 2);
            entity.Property(amendment => amendment.Reason).HasMaxLength(500).IsRequired();
            entity.HasIndex(amendment => new { amendment.BookingId, amendment.AmendedAtUtc });
            entity.HasOne(amendment => amendment.Booking)
                .WithMany(booking => booking.Amendments)
                .HasForeignKey(amendment => amendment.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(amendment => amendment.AmendedByUser)
                .WithMany()
                .HasForeignKey(amendment => amendment.AmendedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<HousekeepingTask>(entity =>
        {
            entity.ToTable("housekeeping_tasks", table =>
            {
                table.HasCheckConstraint("CK_housekeeping_tasks_timeline", "\"StartedAtUtc\" IS NULL OR \"AssignedAtUtc\" IS NOT NULL");
                table.HasCheckConstraint("CK_housekeeping_tasks_completion", "(\"Status\" = 'Completed' AND \"CompletedAtUtc\" IS NOT NULL AND \"CompletedByUserId\" IS NOT NULL) OR (\"Status\" <> 'Completed' AND \"CompletedAtUtc\" IS NULL AND \"CompletedByUserId\" IS NULL)");
                table.HasCheckConstraint("CK_housekeeping_tasks_inspection", "\"InspectionPassed\" IS NULL OR (\"Type\" = 'Inspection' AND \"Status\" = 'Completed')");
            });
            entity.HasKey(task => task.Id);
            entity.Property(task => task.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(task => task.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(task => task.Priority).HasConversion<string>().HasMaxLength(20);
            entity.Property(task => task.Notes).HasMaxLength(1000).IsRequired();
            entity.HasIndex(task => new { task.Status, task.Priority, task.CreatedAtUtc });
            entity.HasIndex(task => new { task.RoomId, task.Status });
            entity.HasOne(task => task.Room)
                .WithMany(room => room.HousekeepingTasks)
                .HasForeignKey(task => task.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(task => task.Booking)
                .WithMany()
                .HasForeignKey(task => task.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(task => task.CreatedByUser)
                .WithMany()
                .HasForeignKey(task => task.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(task => task.AssignedToUser)
                .WithMany()
                .HasForeignKey(task => task.AssignedToUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(task => task.CompletedByUser)
                .WithMany()
                .HasForeignKey(task => task.CompletedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MaintenanceWorkOrder>(entity =>
        {
            entity.ToTable("maintenance_work_orders", table =>
            {
                table.HasCheckConstraint("CK_maintenance_work_orders_dates", "\"OutOfOrderUntil\" > \"OutOfOrderFrom\"");
                table.HasCheckConstraint("CK_maintenance_work_orders_resolution", "(\"Status\" IN ('Resolved','Cancelled') AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL) OR (\"Status\" NOT IN ('Resolved','Cancelled') AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL)");
            });
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Title).HasMaxLength(160).IsRequired();
            entity.Property(order => order.Description).HasMaxLength(2000).IsRequired();
            entity.Property(order => order.Priority).HasConversion<string>().HasMaxLength(20);
            entity.Property(order => order.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(order => order.ResolutionNotes).HasMaxLength(1000);
            entity.HasIndex(order => new { order.Status, order.Priority, order.CreatedAtUtc });
            entity.HasIndex(order => new { order.RoomId, order.Status });
            entity.HasIndex(order => order.InventoryClosureId).IsUnique()
                .HasFilter("\"InventoryClosureId\" IS NOT NULL");
            entity.HasOne(order => order.Room)
                .WithMany(room => room.MaintenanceWorkOrders)
                .HasForeignKey(order => order.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(order => order.InventoryClosure)
                .WithOne()
                .HasForeignKey<MaintenanceWorkOrder>(order => order.InventoryClosureId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(order => order.CreatedByUser)
                .WithMany()
                .HasForeignKey(order => order.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(order => order.AssignedToUser)
                .WithMany()
                .HasForeignKey(order => order.AssignedToUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(order => order.ResolvedByUser)
                .WithMany()
                .HasForeignKey(order => order.ResolvedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<NightAudit>(entity =>
        {
            entity.ToTable("night_audits", table => table.HasCheckConstraint(
                "CK_night_audits_values",
                "\"AvailableRoomNights\" >= 0 AND \"OccupiedRoomNights\" >= 0 AND \"RoomRevenue\" >= 0 AND \"Payments\" >= 0 AND \"Refunds\" >= 0 AND \"Receivables\" >= 0 AND \"GuestCredits\" >= 0 AND \"Adr\" >= 0 AND \"RevPar\" >= 0"));
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.RoomRevenue).HasPrecision(18, 2);
            entity.Property(audit => audit.Payments).HasPrecision(18, 2);
            entity.Property(audit => audit.Refunds).HasPrecision(18, 2);
            entity.Property(audit => audit.Receivables).HasPrecision(18, 2);
            entity.Property(audit => audit.GuestCredits).HasPrecision(18, 2);
            entity.Property(audit => audit.Adr).HasPrecision(18, 2);
            entity.Property(audit => audit.RevPar).HasPrecision(18, 2);
            entity.Property(audit => audit.SnapshotJson).HasColumnType("jsonb");
            entity.HasIndex(audit => audit.BusinessDate).IsUnique();
            entity.HasOne(audit => audit.ClosedByUser)
                .WithMany()
                .HasForeignKey(audit => audit.ClosedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<GuestNote>(entity =>
        {
            entity.ToTable("guest_notes");
            entity.HasKey(note => note.Id);
            entity.Property(note => note.GuestId).HasMaxLength(20);
            entity.Property(note => note.Body).HasMaxLength(1000).IsRequired();
            entity.HasIndex(note => new { note.GuestId, note.CreatedAtUtc });
            entity.HasOne(note => note.Guest)
                .WithMany(guest => guest.Notes)
                .HasForeignKey(note => note.GuestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(note => note.CreatedByUser)
                .WithMany()
                .HasForeignKey(note => note.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<GuestMerge>(entity =>
        {
            entity.ToTable("guest_merges", table => table.HasCheckConstraint(
                "CK_guest_merges_distinct_guests",
                "\"PrimaryGuestId\" <> \"DuplicateGuestId\""));
            entity.HasKey(merge => merge.Id);
            entity.Property(merge => merge.PrimaryGuestId).HasMaxLength(20);
            entity.Property(merge => merge.DuplicateGuestId).HasMaxLength(20);
            entity.Property(merge => merge.EvidenceType).HasMaxLength(50).IsRequired();
            entity.Property(merge => merge.Reason).HasMaxLength(160).IsRequired();
            entity.HasIndex(merge => merge.DuplicateGuestId).IsUnique();
            entity.HasIndex(merge => new { merge.PrimaryGuestId, merge.MergedAtUtc });
            entity.HasOne(merge => merge.PrimaryGuest)
                .WithMany()
                .HasForeignKey(merge => merge.PrimaryGuestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(merge => merge.DuplicateGuest)
                .WithMany()
                .HasForeignKey(merge => merge.DuplicateGuestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(merge => merge.MergedByUser)
                .WithMany()
                .HasForeignKey(merge => merge.MergedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DistributionChannel>(entity =>
        {
            entity.ToTable("distribution_channels");
            entity.HasKey(channel => channel.Id);
            entity.Property(channel => channel.Code).HasMaxLength(40).IsRequired();
            entity.Property(channel => channel.Name).HasMaxLength(120).IsRequired();
            entity.HasIndex(channel => channel.Code).IsUnique();
        });

        builder.Entity<ChannelEvent>(entity =>
        {
            entity.ToTable("channel_events", table =>
            {
                table.HasCheckConstraint("CK_channel_events_attempt_count", "\"AttemptCount\" >= 0");
                table.HasCheckConstraint("CK_channel_events_state", "(\"Status\" = 'Pending' AND \"ProcessedAtUtc\" IS NULL AND \"LastError\" IS NULL) OR (\"Status\" = 'Processed' AND \"ProcessedAtUtc\" IS NOT NULL AND \"LastError\" IS NULL) OR (\"Status\" IN ('Failed','DeadLetter') AND \"ProcessedAtUtc\" IS NULL AND \"LastError\" IS NOT NULL)");
            });
            entity.HasKey(channelEvent => channelEvent.Id);
            entity.Property(channelEvent => channelEvent.Direction).HasConversion<string>().HasMaxLength(20);
            entity.Property(channelEvent => channelEvent.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(channelEvent => channelEvent.EventType).HasMaxLength(80).IsRequired();
            entity.Property(channelEvent => channelEvent.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(channelEvent => channelEvent.ExternalReservationId).HasMaxLength(160);
            entity.Property(channelEvent => channelEvent.PayloadJson).HasColumnType("jsonb");
            entity.Property(channelEvent => channelEvent.LastError).HasMaxLength(1000);
            entity.HasIndex(channelEvent => new { channelEvent.ChannelId, channelEvent.IdempotencyKey }).IsUnique();
            entity.HasIndex(channelEvent => new { channelEvent.Status, channelEvent.Direction, channelEvent.CreatedAtUtc });
            entity.HasOne(channelEvent => channelEvent.Channel)
                .WithMany(channel => channel.Events)
                .HasForeignKey(channelEvent => channelEvent.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ChannelReservationMapping>(entity =>
        {
            entity.ToTable("channel_reservation_mappings");
            entity.HasKey(mapping => mapping.Id);
            entity.Property(mapping => mapping.ExternalReservationId).HasMaxLength(160).IsRequired();
            entity.HasIndex(mapping => new { mapping.ChannelId, mapping.ExternalReservationId }).IsUnique();
            entity.HasIndex(mapping => new { mapping.ChannelId, mapping.BookingId }).IsUnique();
            entity.HasOne(mapping => mapping.Channel)
                .WithMany()
                .HasForeignKey(mapping => mapping.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(mapping => mapping.Booking)
                .WithMany()
                .HasForeignKey(mapping => mapping.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(mapping => mapping.LinkedByUser)
                .WithMany()
                .HasForeignKey(mapping => mapping.LinkedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PrivacyRequest>(entity =>
        {
            entity.ToTable("privacy_requests", table =>
            {
                table.HasCheckConstraint(
                    "CK_privacy_requests_resolution_consistent",
                    "(\"Status\" IN ('Completed', 'Rejected') AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL) OR (\"Status\" IN ('Pending', 'InProgress') AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_privacy_requests_identity_consistent",
                    "(\"IdentityVerifiedAtUtc\" IS NULL AND \"IdentityVerifiedByUserId\" IS NULL AND \"IdentityVerificationReference\" IS NULL) OR (\"IdentityVerifiedAtUtc\" IS NOT NULL AND \"IdentityVerifiedByUserId\" IS NOT NULL AND \"IdentityVerificationReference\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_privacy_requests_fulfillment_consistent",
                    "(\"Status\" = 'Completed' AND \"FulfilledAtUtc\" IS NOT NULL AND \"FulfillmentEvidenceReference\" IS NOT NULL AND \"FulfillmentDigest\" IS NOT NULL) OR (\"Status\" <> 'Completed' AND \"FulfilledAtUtc\" IS NULL AND \"FulfillmentEvidenceReference\" IS NULL AND \"FulfillmentDigest\" IS NULL AND \"ExportGeneratedAtUtc\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_privacy_requests_due_date",
                    "\"DueAtUtc\" >= \"RequestedAtUtc\"");
            });
            entity.HasKey(request => request.Id);
            entity.Property(request => request.GuestId).HasMaxLength(20).IsRequired();
            entity.Property(request => request.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(request => request.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(request => request.Details).HasMaxLength(2000);
            entity.Property(request => request.ResolutionNotes).HasMaxLength(2000);
            entity.Property(request => request.IdentityVerificationReference).HasMaxLength(160);
            entity.Property(request => request.FulfillmentEvidenceReference).HasMaxLength(500);
            entity.Property(request => request.FulfillmentDigest).HasMaxLength(64);
            entity.HasIndex(request => new { request.GuestId, request.RequestedAtUtc });
            entity.HasIndex(request => new { request.Status, request.RequestedAtUtc });
            entity.HasIndex(request => new { request.Status, request.DueAtUtc });
            entity.HasIndex(request => new { request.GuestId, request.Type }).IsUnique()
                .HasFilter("\"Status\" IN ('Pending', 'InProgress')");
            entity.HasOne(request => request.Guest)
                .WithMany()
                .HasForeignKey(request => request.GuestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(request => request.RequestedByUser)
                .WithMany()
                .HasForeignKey(request => request.RequestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(request => request.IdentityVerifiedByUser)
                .WithMany()
                .HasForeignKey(request => request.IdentityVerifiedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(request => request.ResolvedByUser)
                .WithMany()
                .HasForeignKey(request => request.ResolvedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<BookingCodeAllocation>(entity =>
        {
            entity.ToTable("booking_code_allocations");
            entity.HasKey(allocation => allocation.Code);
            // Legacy references created before the short-code rollout can be
            // longer than the new MHS plus six-digit format. The allocation
            // ledger must retain them so no historical reference is reused.
            entity.Property(allocation => allocation.Code).HasMaxLength(30);
            entity.Property(allocation => allocation.AllocatedAtUtc).IsRequired();
            entity.HasIndex(allocation => allocation.AllocatedAtUtc);
        });

        builder.Entity<BookingEmailVerification>(entity =>
        {
            entity.ToTable("booking_email_verifications", table => table.HasCheckConstraint(
                "CK_booking_email_verifications_valid_window",
                "\"ExpiresAtUtc\" > \"CreatedAtUtc\""));
            entity.HasKey(verification => verification.Id);
            entity.Property(verification => verification.Email).HasMaxLength(254).IsRequired();
            entity.Property(verification => verification.TokenHash).HasMaxLength(44).IsRequired();
            entity.HasIndex(verification => verification.TokenHash).IsUnique();
            entity.HasIndex(verification => new
            {
                verification.Email,
                verification.CreatedAtUtc
            });
            entity.HasIndex(verification => new
            {
                verification.ExpiresAtUtc,
                verification.Id
            }).HasFilter("\"ConsumedAtUtc\" IS NULL");
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.Property(log => log.Action).HasMaxLength(100).IsRequired();
            entity.Property(log => log.EntityType).HasMaxLength(100).IsRequired();
            entity.Property(log => log.EntityId).HasMaxLength(160).IsRequired();
            entity.Property(log => log.OldDataJson).HasColumnType("jsonb");
            entity.Property(log => log.NewDataJson).HasColumnType("jsonb");
            entity.HasIndex(log => log.CreatedAt);
            entity.HasIndex(log => new { log.EntityType, log.EntityId, log.CreatedAt });
        });

        builder.Entity<VisitRecord>(entity =>
        {
            entity.ToTable("visit_records");
            entity.Property(record => record.GuestId).HasMaxLength(20).IsRequired();
            entity.Property(record => record.GuestName).HasMaxLength(160).IsRequired();
            entity.Property(record => record.RoomNumber).HasMaxLength(30).IsRequired();
            entity.Property(record => record.BookingCode).HasMaxLength(30).IsRequired();
            entity.Property(record => record.Action).HasMaxLength(40).IsRequired();
            entity.Property(record => record.AuthorizedBy).HasMaxLength(160).IsRequired();
            entity.HasIndex(record => record.Timestamp);
            entity.HasIndex(record => new { record.Timestamp, record.Id });
            entity.HasIndex(record => new { record.BookingCode, record.Timestamp });
        });

        builder.Entity<Notification>(entity =>
        {
            entity.ToTable("notifications");
            entity.Property(notification => notification.Title).HasMaxLength(200).IsRequired();
            entity.Property(notification => notification.Message).HasMaxLength(2000).IsRequired();
            entity.Property(notification => notification.BookingCode).HasMaxLength(30);
            entity.HasIndex(notification => new { notification.UserId, notification.IsRead, notification.CreatedAt });
        });

        builder.Entity<NotificationReceipt>(entity =>
        {
            entity.ToTable("notification_receipts");
            entity.HasKey(receipt => new { receipt.NotificationId, receipt.UserId });
            entity.HasIndex(receipt => new { receipt.UserId, receipt.ReadAtUtc });
            entity.HasOne(receipt => receipt.Notification)
                .WithMany()
                .HasForeignKey(receipt => receipt.NotificationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(receipt => receipt.User)
                .WithMany()
                .HasForeignKey(receipt => receipt.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MonnifyTransaction>(entity =>
        {
            entity.ToTable("monnify_transactions");
            entity.HasKey(transaction => transaction.Id);
            entity.HasIndex(transaction => transaction.TransactionReference).IsUnique();
            entity.HasIndex(transaction => transaction.MonnifyReference).IsUnique()
                .HasFilter("\"MonnifyReference\" IS NOT NULL");
            entity.HasIndex(transaction => transaction.BookingId).IsUnique()
                .HasFilter("\"BookingId\" IS NOT NULL");
            entity.HasIndex(transaction => new { transaction.BookingCode, transaction.Status });
            entity.Property(transaction => transaction.BookingCode).HasMaxLength(30).IsRequired();
            entity.Property(transaction => transaction.TransactionReference).HasMaxLength(160).IsRequired();
            entity.Property(transaction => transaction.MonnifyReference).HasMaxLength(160);
            entity.Property(transaction => transaction.Status).HasMaxLength(40).IsRequired();
            entity.Property(transaction => transaction.PaymentMethod).HasMaxLength(50);
            entity.Property(transaction => transaction.Source).HasMaxLength(30);
            entity.Property(transaction => transaction.Amount).HasPrecision(18, 2);
            entity.Property(transaction => transaction.Fee).HasPrecision(18, 2);
            entity.Property(transaction => transaction.SettledAmount).HasPrecision(18, 2);
            entity.HasOne(transaction => transaction.Booking)
                .WithMany()
                .HasForeignKey(transaction => transaction.BookingId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<EmailOutboxMessage>(entity =>
        {
            entity.ToTable("email_outbox");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Template).HasMaxLength(50).IsRequired();
            entity.Property(message => message.Recipient).HasMaxLength(254).IsRequired();
            entity.Property(message => message.DataSubjectGuestId).HasMaxLength(20);
            entity.Property(message => message.ProtectedPayload).HasColumnType("text").IsRequired();
            entity.Property(message => message.LastErrorCode).HasMaxLength(80);
            entity.Property(message => message.DeliveryFailureMetadataJson).HasColumnType("jsonb");
            entity.HasIndex(message => new
            {
                message.NextAttemptAtUtc,
                message.LockedUntilUtc,
                message.AttemptCount
            });
            entity.HasIndex(message => message.CreatedAtUtc);
            entity.HasIndex(message => message.QuarantinedAtUtc);
            entity.HasIndex(message => message.DataSubjectGuestId);
            entity.HasOne<Guest>()
                .WithMany()
                .HasForeignKey(message => message.DataSubjectGuestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AddOnService>(entity =>
        {
            entity.ToTable("addon_services", table => table.HasCheckConstraint(
                "CK_addon_services_price_positive",
                "\"Price\" > 0"));
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Name).HasMaxLength(120).IsRequired();
            entity.Property(a => a.Description).HasMaxLength(500);
            entity.Property(a => a.Category).HasConversion<string>().HasMaxLength(40);
            entity.Property(a => a.Price).HasPrecision(18, 2);
            entity.HasIndex(a => new { a.IsActive, a.Category });
        });

        builder.Entity<BookingAddOn>(entity =>
        {
            entity.ToTable("booking_addons", table =>
            {
                table.HasCheckConstraint(
                    "CK_booking_addons_quantity_positive",
                    "\"Quantity\" > 0");
                table.HasCheckConstraint(
                    "CK_booking_addons_unit_price_positive",
                    "\"UnitPrice\" > 0");
                table.HasCheckConstraint(
                    "CK_booking_addons_total_matches_quantity",
                    "\"TotalPrice\" = \"UnitPrice\" * \"Quantity\"");
            });
            entity.HasKey(b => b.Id);
            entity.Property(b => b.UnitPrice).HasPrecision(18, 2);
            entity.Property(b => b.TotalPrice).HasPrecision(18, 2);
            entity.Property(b => b.Notes).HasMaxLength(300);
            entity.HasIndex(b => new { b.BookingId, b.AddedAtUtc });
            entity.HasIndex(b => b.AddOnServiceId);
            entity.HasOne(b => b.Booking)
                .WithMany(booking => booking.AddOns)
                .HasForeignKey(b => b.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(b => b.AddOnService)
                .WithMany()
                .HasForeignKey(b => b.AddOnServiceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
