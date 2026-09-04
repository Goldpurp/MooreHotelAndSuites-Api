using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MooreHotels.Domain.Entities;
using System.Text.Json;

namespace MooreHotels.Infrastructure.Persistence;

public sealed class MooreHotelsDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public MooreHotelsDbContext(DbContextOptions<MooreHotelsDbContext> options) : base(options) { }

    public DbSet<Room> Rooms => Set<Room>();
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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var listConverter = new ValueConverter<List<string>, string>(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());
        var listComparer = new ValueComparer<List<string>>(
            (left, right) => ReferenceEquals(left, right) ||
                             left != null && right != null && left.SequenceEqual(right),
            value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
            value => value.ToList());

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.ToTable("users", table => table.HasCheckConstraint(
                "CK_users_privacy_acceptance_consistent",
                "(\"PrivacyPolicyVersion\" IS NULL AND \"PrivacyPolicyAcceptedAtUtc\" IS NULL) OR (\"PrivacyPolicyVersion\" IS NOT NULL AND \"PrivacyPolicyAcceptedAtUtc\" IS NOT NULL)"));
            entity.Property(user => user.Name).HasMaxLength(160).IsRequired();
            entity.Property(user => user.AvatarUrl).HasMaxLength(2048);
            entity.Property(user => user.AvatarPublicId).HasMaxLength(512);
            entity.Property(user => user.Department).HasMaxLength(80);
            entity.Property(user => user.GuestId).HasMaxLength(20);
            entity.Property(user => user.PrivacyPolicyVersion).HasMaxLength(80);
            entity.Property(user => user.Role).HasConversion<string>().HasMaxLength(30);
            entity.Property(user => user.Status).HasConversion<string>().HasMaxLength(30);
            entity.HasIndex(user => new { user.Status, user.Role });
            entity.HasIndex(user => user.GuestId).IsUnique()
                .HasFilter("\"GuestId\" IS NOT NULL");
            entity.HasOne(user => user.GuestProfile)
                .WithMany()
                .HasForeignKey(user => user.GuestId)
                .OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<IdentityRole<Guid>>(entity => entity.ToTable("roles"));
        builder.Entity<IdentityUserRole<Guid>>(entity => entity.ToTable("user_roles"));

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
        });

        builder.Entity<RoomImage>(entity =>
        {
            entity.ToTable("room_images");
            entity.HasKey(image => image.Id);
            entity.Property(image => image.Url).HasMaxLength(2048).IsRequired();
            entity.Property(image => image.PublicId).HasMaxLength(512).IsRequired();
            entity.HasIndex(image => image.PublicId).IsUnique();
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
            entity.ToTable("guests");
            entity.HasKey(guest => guest.Id);
            entity.Property(guest => guest.Id).HasMaxLength(20);
            entity.Property(guest => guest.FirstName).HasMaxLength(80).IsRequired();
            entity.Property(guest => guest.LastName).HasMaxLength(80).IsRequired();
            entity.Property(guest => guest.Email).HasMaxLength(254).IsRequired();
            entity.Property(guest => guest.Phone).HasMaxLength(30).IsRequired();
            entity.Property(guest => guest.AvatarUrl).HasMaxLength(2048);
            entity.HasIndex(guest => guest.Email);
            entity.HasIndex(guest => new { guest.Email, guest.FirstName, guest.LastName });
            entity.HasIndex(guest => guest.AnonymizedAtUtc);
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
            entity.Property(booking => booking.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.PaymentStatus).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.PaymentMethod).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.TransactionReference).HasMaxLength(160);
            entity.Property(booking => booking.PaymentProviderReference).HasMaxLength(160);
            entity.Property(booking => booking.PaymentCheckoutUrl).HasMaxLength(2048);
            entity.Property(booking => booking.PaymentConfirmationMethod).HasMaxLength(50);
            entity.Property(booking => booking.RefundReference).HasMaxLength(160);
            entity.Property(booking => booking.RefundAmount).HasPrecision(18, 2);
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
            entity.Property(booking => booking.StatusHistoryJson).HasColumnType("jsonb");
            entity.Property(booking => booking.GuestAccessTokenHash).HasMaxLength(44);
            entity.Property(booking => booking.PrivacyPolicyVersion).HasMaxLength(80);
            entity.Property(booking => booking.BookingTermsVersion).HasMaxLength(80);
            entity.HasOne(booking => booking.Room)
                .WithMany()
                .HasForeignKey(booking => booking.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
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
                    "(\"RoomId\" IS NOT NULL AND \"RoomCategory\" IS NULL) OR (\"RoomId\" IS NULL AND \"RoomCategory\" IS NOT NULL)");
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
            entity.HasIndex(rate => new { rate.RatePlanId, rate.RoomId, rate.StayDate })
                .IsUnique()
                .HasFilter("\"RoomId\" IS NOT NULL");
            entity.HasIndex(rate => new { rate.RatePlanId, rate.RoomCategory, rate.StayDate })
                .IsUnique()
                .HasFilter("\"RoomCategory\" IS NOT NULL");
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
            entity.HasOne(quote => quote.Room).WithMany().HasForeignKey(quote => quote.RoomId).OnDelete(DeleteBehavior.Restrict);
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

        builder.Entity<PrivacyRequest>(entity =>
        {
            entity.ToTable("privacy_requests", table => table.HasCheckConstraint(
                "CK_privacy_requests_resolution_consistent",
                "(\"Status\" IN ('Completed', 'Rejected') AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL) OR (\"Status\" IN ('Pending', 'InProgress') AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL)"));
            entity.HasKey(request => request.Id);
            entity.Property(request => request.GuestId).HasMaxLength(20).IsRequired();
            entity.Property(request => request.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(request => request.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(request => request.Details).HasMaxLength(2000);
            entity.Property(request => request.ResolutionNotes).HasMaxLength(2000);
            entity.HasIndex(request => new { request.GuestId, request.RequestedAtUtc });
            entity.HasIndex(request => new { request.Status, request.RequestedAtUtc });
            entity.HasOne(request => request.Guest)
                .WithMany()
                .HasForeignKey(request => request.GuestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(request => request.RequestedByUser)
                .WithMany()
                .HasForeignKey(request => request.RequestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
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
            entity.Property(message => message.ProtectedPayload).HasColumnType("text").IsRequired();
            entity.Property(message => message.LastErrorCode).HasMaxLength(80);
            entity.HasIndex(message => new
            {
                message.NextAttemptAtUtc,
                message.LockedUntilUtc,
                message.AttemptCount
            });
            entity.HasIndex(message => message.CreatedAtUtc);
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
