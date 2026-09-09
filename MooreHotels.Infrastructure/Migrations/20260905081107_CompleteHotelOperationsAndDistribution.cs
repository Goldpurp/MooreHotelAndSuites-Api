using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteHotelOperationsAndDistribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedAtUtc",
                table: "guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MergedAtUtc",
                table: "guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MergedByUserId",
                table: "guests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergedIntoGuestId",
                table: "guests",
                type: "character varying(20)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "guests",
                type: "character varying(254)",
                maxLength: 254,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedPhone",
                table: "guests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "PhoneVerifiedAtUtc",
                table: "guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferencesJson",
                table: "guests",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<decimal>(
                name: "CancellationPenaltyAmount",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CancellationPenaltyPercent",
                table: "bookings",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DepositPercent",
                table: "bookings",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m);

            migrationBuilder.AddColumn<int>(
                name: "FreeCancellationHours",
                table: "bookings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "NoShowPenaltyAmount",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "NoShowPenaltyPercent",
                table: "bookings",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m);

            migrationBuilder.AddColumn<string>(
                name: "ReservationPolicyVersion",
                table: "bookings",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "LEGACY-FULL-REFUND");

            migrationBuilder.Sql(
                """
                UPDATE guests
                SET "NormalizedEmail" = lower(btrim("Email")),
                    "NormalizedPhone" = CASE
                        WHEN left(ltrim("Phone"), 1) = '+'
                            THEN '+' || regexp_replace("Phone", '[^0-9]', '', 'g')
                        ELSE regexp_replace("Phone", '[^0-9]', '', 'g')
                    END,
                    "PreferencesJson" = '{}'::jsonb;
                """);

            migrationBuilder.CreateTable(
                name: "booking_amendments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousStateJson = table.Column<string>(type: "jsonb", nullable: false),
                    NewStateJson = table.Column<string>(type: "jsonb", nullable: false),
                    PreviousAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NewAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PriceDifference = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RoomTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomQuantity = table.Column<int>(type: "integer", nullable: false),
                    CheckIn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CheckOut = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AdultCount = table.Column<int>(type: "integer", nullable: false),
                    ChildCount = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AmendedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AmendedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_amendments", x => x.Id);
                    table.CheckConstraint("CK_booking_amendments_amounts", "\"PreviousAmount\" >= 0 AND \"NewAmount\" >= 0 AND \"PriceDifference\" = \"NewAmount\" - \"PreviousAmount\"");
                    table.CheckConstraint("CK_booking_amendments_dates", "\"CheckOut\" > \"CheckIn\"");
                    table.CheckConstraint("CK_booking_amendments_occupancy", "\"AdultCount\" >= 1 AND \"ChildCount\" >= 0");
                    table.CheckConstraint("CK_booking_amendments_room_quantity", "\"RoomQuantity\" BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_booking_amendments_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_booking_amendments_users_AmendedByUserId",
                        column: x => x.AmendedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "distribution_channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_distribution_channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "guest_merges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrimaryGuestId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DuplicateGuestId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EvidenceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MovedBookingCount = table.Column<int>(type: "integer", nullable: false),
                    MergedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MergedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guest_merges", x => x.Id);
                    table.CheckConstraint("CK_guest_merges_distinct_guests", "\"PrimaryGuestId\" <> \"DuplicateGuestId\"");
                    table.ForeignKey(
                        name: "FK_guest_merges_guests_DuplicateGuestId",
                        column: x => x.DuplicateGuestId,
                        principalTable: "guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guest_merges_guests_PrimaryGuestId",
                        column: x => x.PrimaryGuestId,
                        principalTable: "guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guest_merges_users_MergedByUserId",
                        column: x => x.MergedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guest_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsSensitive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guest_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guest_notes_guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guest_notes_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "housekeeping_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    InspectionPassed = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_housekeeping_tasks", x => x.Id);
                    table.CheckConstraint("CK_housekeeping_tasks_completion", "(\"Status\" = 'Completed' AND \"CompletedAtUtc\" IS NOT NULL AND \"CompletedByUserId\" IS NOT NULL) OR (\"Status\" <> 'Completed' AND \"CompletedAtUtc\" IS NULL AND \"CompletedByUserId\" IS NULL)");
                    table.CheckConstraint("CK_housekeeping_tasks_inspection", "\"InspectionPassed\" IS NULL OR (\"Type\" = 'Inspection' AND \"Status\" = 'Completed')");
                    table.CheckConstraint("CK_housekeeping_tasks_timeline", "\"StartedAtUtc\" IS NULL OR \"AssignedAtUtc\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_users_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_users_CompletedByUserId",
                        column: x => x.CompletedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_work_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryClosureId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OutOfOrderFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    OutOfOrderUntil = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_work_orders", x => x.Id);
                    table.CheckConstraint("CK_maintenance_work_orders_dates", "\"OutOfOrderUntil\" > \"OutOfOrderFrom\"");
                    table.CheckConstraint("CK_maintenance_work_orders_resolution", "(\"Status\" IN ('Resolved','Cancelled') AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL) OR (\"Status\" NOT IN ('Resolved','Cancelled') AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_maintenance_work_orders_room_inventory_closures_InventoryCl~",
                        column: x => x.InventoryClosureId,
                        principalTable: "room_inventory_closures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_maintenance_work_orders_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_maintenance_work_orders_users_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_maintenance_work_orders_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_maintenance_work_orders_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "night_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AvailableRoomNights = table.Column<int>(type: "integer", nullable: false),
                    OccupiedRoomNights = table.Column<int>(type: "integer", nullable: false),
                    RoomRevenue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Payments = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Refunds = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Receivables = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    GuestCredits = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Adr = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RevPar = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_night_audits", x => x.Id);
                    table.CheckConstraint("CK_night_audits_values", "\"AvailableRoomNights\" >= 0 AND \"OccupiedRoomNights\" >= 0 AND \"RoomRevenue\" >= 0 AND \"Payments\" >= 0 AND \"Refunds\" >= 0 AND \"Receivables\" >= 0 AND \"GuestCredits\" >= 0 AND \"Adr\" >= 0 AND \"RevPar\" >= 0");
                    table.ForeignKey(
                        name: "FK_night_audits_users_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ExternalReservationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_events", x => x.Id);
                    table.CheckConstraint("CK_channel_events_attempt_count", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_channel_events_state", "(\"Status\" = 'Pending' AND \"ProcessedAtUtc\" IS NULL AND \"LastError\" IS NULL) OR (\"Status\" = 'Processed' AND \"ProcessedAtUtc\" IS NOT NULL AND \"LastError\" IS NULL) OR (\"Status\" IN ('Failed','DeadLetter') AND \"ProcessedAtUtc\" IS NULL AND \"LastError\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_channel_events_distribution_channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "distribution_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_reservation_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalReservationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LinkedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_reservation_mappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_channel_reservation_mappings_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_channel_reservation_mappings_distribution_channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "distribution_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_channel_reservation_mappings_users_LinkedByUserId",
                        column: x => x.LinkedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                CREATE FUNCTION prevent_operational_history_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Operational history is append-only.'
                        USING ERRCODE = '23514',
                              CONSTRAINT = 'CK_operational_history_immutable';
                END;
                $$;

                CREATE TRIGGER "TR_booking_amendments_immutable"
                BEFORE UPDATE OR DELETE ON booking_amendments
                FOR EACH ROW EXECUTE FUNCTION prevent_operational_history_mutation();

                CREATE TRIGGER "TR_guest_merges_immutable"
                BEFORE UPDATE OR DELETE ON guest_merges
                FOR EACH ROW EXECUTE FUNCTION prevent_operational_history_mutation();

                CREATE TRIGGER "TR_night_audits_immutable"
                BEFORE UPDATE OR DELETE ON night_audits
                FOR EACH ROW EXECUTE FUNCTION prevent_operational_history_mutation();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_guests_MergedByUserId",
                table: "guests",
                column: "MergedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_guests_MergedIntoGuestId",
                table: "guests",
                column: "MergedIntoGuestId");

            migrationBuilder.CreateIndex(
                name: "IX_guests_NormalizedEmail_MergedIntoGuestId",
                table: "guests",
                columns: new[] { "NormalizedEmail", "MergedIntoGuestId" });

            migrationBuilder.CreateIndex(
                name: "IX_guests_NormalizedPhone_MergedIntoGuestId",
                table: "guests",
                columns: new[] { "NormalizedPhone", "MergedIntoGuestId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_guests_merge_state",
                table: "guests",
                sql: "(\"MergedIntoGuestId\" IS NULL AND \"MergedAtUtc\" IS NULL AND \"MergedByUserId\" IS NULL) OR (\"MergedIntoGuestId\" IS NOT NULL AND \"MergedAtUtc\" IS NOT NULL AND \"MergedByUserId\" IS NOT NULL AND \"MergedIntoGuestId\" <> \"Id\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bookings_reservation_policy",
                table: "bookings",
                sql: "\"FreeCancellationHours\" BETWEEN 0 AND 720 AND \"CancellationPenaltyPercent\" BETWEEN 0 AND 100 AND \"DepositPercent\" BETWEEN 0 AND 100 AND \"NoShowPenaltyPercent\" BETWEEN 0 AND 100 AND \"CancellationPenaltyAmount\" >= 0 AND \"NoShowPenaltyAmount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_booking_amendments_AmendedByUserId",
                table: "booking_amendments",
                column: "AmendedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_booking_amendments_BookingId_AmendedAtUtc",
                table: "booking_amendments",
                columns: new[] { "BookingId", "AmendedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_events_ChannelId_IdempotencyKey",
                table: "channel_events",
                columns: new[] { "ChannelId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_events_Status_Direction_CreatedAtUtc",
                table: "channel_events",
                columns: new[] { "Status", "Direction", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_reservation_mappings_BookingId",
                table: "channel_reservation_mappings",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_channel_reservation_mappings_ChannelId_BookingId",
                table: "channel_reservation_mappings",
                columns: new[] { "ChannelId", "BookingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_reservation_mappings_ChannelId_ExternalReservationId",
                table: "channel_reservation_mappings",
                columns: new[] { "ChannelId", "ExternalReservationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_reservation_mappings_LinkedByUserId",
                table: "channel_reservation_mappings",
                column: "LinkedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_distribution_channels_Code",
                table: "distribution_channels",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guest_merges_DuplicateGuestId",
                table: "guest_merges",
                column: "DuplicateGuestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guest_merges_MergedByUserId",
                table: "guest_merges",
                column: "MergedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_guest_merges_PrimaryGuestId_MergedAtUtc",
                table: "guest_merges",
                columns: new[] { "PrimaryGuestId", "MergedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_guest_notes_CreatedByUserId",
                table: "guest_notes",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_guest_notes_GuestId_CreatedAtUtc",
                table: "guest_notes",
                columns: new[] { "GuestId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_AssignedToUserId",
                table: "housekeeping_tasks",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_BookingId",
                table: "housekeeping_tasks",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_CompletedByUserId",
                table: "housekeeping_tasks",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_CreatedByUserId",
                table: "housekeeping_tasks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_RoomId_Status",
                table: "housekeeping_tasks",
                columns: new[] { "RoomId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_Status_Priority_CreatedAtUtc",
                table: "housekeeping_tasks",
                columns: new[] { "Status", "Priority", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_work_orders_AssignedToUserId",
                table: "maintenance_work_orders",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_work_orders_CreatedByUserId",
                table: "maintenance_work_orders",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_work_orders_InventoryClosureId",
                table: "maintenance_work_orders",
                column: "InventoryClosureId",
                unique: true,
                filter: "\"InventoryClosureId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_work_orders_ResolvedByUserId",
                table: "maintenance_work_orders",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_work_orders_RoomId_Status",
                table: "maintenance_work_orders",
                columns: new[] { "RoomId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_work_orders_Status_Priority_CreatedAtUtc",
                table: "maintenance_work_orders",
                columns: new[] { "Status", "Priority", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_night_audits_BusinessDate",
                table: "night_audits",
                column: "BusinessDate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_night_audits_ClosedByUserId",
                table: "night_audits",
                column: "ClosedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_guests_guests_MergedIntoGuestId",
                table: "guests",
                column: "MergedIntoGuestId",
                principalTable: "guests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guests_users_MergedByUserId",
                table: "guests",
                column: "MergedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_booking_amendments_immutable" ON booking_amendments;
                DROP TRIGGER IF EXISTS "TR_guest_merges_immutable" ON guest_merges;
                DROP TRIGGER IF EXISTS "TR_night_audits_immutable" ON night_audits;
                DROP FUNCTION IF EXISTS prevent_operational_history_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_guests_guests_MergedIntoGuestId",
                table: "guests");

            migrationBuilder.DropForeignKey(
                name: "FK_guests_users_MergedByUserId",
                table: "guests");

            migrationBuilder.DropTable(
                name: "booking_amendments");

            migrationBuilder.DropTable(
                name: "channel_events");

            migrationBuilder.DropTable(
                name: "channel_reservation_mappings");

            migrationBuilder.DropTable(
                name: "guest_merges");

            migrationBuilder.DropTable(
                name: "guest_notes");

            migrationBuilder.DropTable(
                name: "housekeeping_tasks");

            migrationBuilder.DropTable(
                name: "maintenance_work_orders");

            migrationBuilder.DropTable(
                name: "night_audits");

            migrationBuilder.DropTable(
                name: "distribution_channels");

            migrationBuilder.DropIndex(
                name: "IX_guests_MergedByUserId",
                table: "guests");

            migrationBuilder.DropIndex(
                name: "IX_guests_MergedIntoGuestId",
                table: "guests");

            migrationBuilder.DropIndex(
                name: "IX_guests_NormalizedEmail_MergedIntoGuestId",
                table: "guests");

            migrationBuilder.DropIndex(
                name: "IX_guests_NormalizedPhone_MergedIntoGuestId",
                table: "guests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_guests_merge_state",
                table: "guests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bookings_reservation_policy",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "EmailVerifiedAtUtc",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "MergedAtUtc",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "MergedByUserId",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "MergedIntoGuestId",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "NormalizedPhone",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "PhoneVerifiedAtUtc",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "PreferencesJson",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "CancellationPenaltyAmount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "CancellationPenaltyPercent",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "DepositPercent",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "FreeCancellationHours",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "NoShowPenaltyAmount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "NoShowPenaltyPercent",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "ReservationPolicyVersion",
                table: "bookings");
        }
    }
}
