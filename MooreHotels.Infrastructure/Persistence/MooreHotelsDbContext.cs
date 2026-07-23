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
    public DbSet<BookingCodeAllocation> BookingCodeAllocations => Set<BookingCodeAllocation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<VisitRecord> VisitRecords => Set<VisitRecord>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<MonnifyTransaction> MonnifyTransactions => Set<MonnifyTransaction>();

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
            entity.ToTable("users");
            entity.Property(user => user.Name).HasMaxLength(160).IsRequired();
            entity.Property(user => user.AvatarUrl).HasMaxLength(2048);
            entity.Property(user => user.AvatarPublicId).HasMaxLength(512);
            entity.Property(user => user.Department).HasMaxLength(80);
            entity.Property(user => user.GuestId).HasMaxLength(20);
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
        });

        builder.Entity<Booking>(entity =>
        {
            entity.ToTable("bookings", table => table.HasCheckConstraint(
                "CK_bookings_valid_dates",
                "\"CheckOut\" > \"CheckIn\""));
            entity.HasIndex(booking => booking.BookingCode).IsUnique();
            entity.HasIndex(booking => booking.TransactionReference).IsUnique()
                .HasFilter("\"TransactionReference\" IS NOT NULL");
            entity.HasIndex(booking => booking.PaymentProviderReference).IsUnique()
                .HasFilter("\"PaymentProviderReference\" IS NOT NULL");
            entity.HasIndex(booking => new { booking.RoomId, booking.CheckIn, booking.CheckOut, booking.Status });
            entity.HasIndex(booking => new { booking.GuestId, booking.CreatedAt });
            entity.HasIndex(booking => new { booking.PaymentStatus, booking.CreatedAt });
            entity.Property(booking => booking.BookingCode).HasMaxLength(30).IsRequired();
            entity.Property(booking => booking.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.PaymentStatus).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.PaymentMethod).HasConversion<string>().HasMaxLength(40);
            entity.Property(booking => booking.TransactionReference).HasMaxLength(160);
            entity.Property(booking => booking.PaymentProviderReference).HasMaxLength(160);
            entity.Property(booking => booking.PaymentCheckoutUrl).HasMaxLength(2048);
            entity.Property(booking => booking.PaymentConfirmationMethod).HasMaxLength(50);
            entity.Property(booking => booking.RefundReference).HasMaxLength(160);
            entity.Property(booking => booking.Notes).HasMaxLength(1000);
            entity.Property(booking => booking.Amount).HasPrecision(18, 2);
            entity.Property(booking => booking.StatusHistoryJson).HasColumnType("jsonb");
            entity.HasOne(booking => booking.Room)
                .WithMany()
                .HasForeignKey(booking => booking.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(booking => booking.Guest)
                .WithMany(guest => guest.Bookings)
                .HasForeignKey(booking => booking.GuestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(booking => booking.PaymentConfirmedByUser)
                .WithMany()
                .HasForeignKey(booking => booking.PaymentConfirmedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(booking => booking.PaymentConfirmedByUserId);
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
    }
}
