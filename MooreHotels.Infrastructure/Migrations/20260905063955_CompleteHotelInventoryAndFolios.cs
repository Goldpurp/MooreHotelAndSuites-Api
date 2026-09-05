using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteHotelInventoryAndFolios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_daily_room_rates_scope",
                table: "daily_room_rates");

            migrationBuilder.AddColumn<Guid>(
                name: "RoomTypeId",
                table: "rooms",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoomTypeId",
                table: "daily_room_rates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomId",
                table: "bookings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<decimal>(
                name: "RefundApprovedAmount",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoomQuantity",
                table: "bookings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "RoomTypeId",
                table: "bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomId",
                table: "booking_quotes",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "RoomQuantity",
                table: "booking_quotes",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "RoomTypeId",
                table: "booking_quotes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "folios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folios", x => x.Id);
                    table.CheckConstraint("CK_folios_closed_state", "(\"Status\" = 'Open' AND \"ClosedAtUtc\" IS NULL AND \"ClosedByUserId\" IS NULL) OR (\"Status\" = 'Closed' AND \"ClosedAtUtc\" IS NOT NULL AND \"ClosedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("CK_folios_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_folios_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_folios_users_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "room_types",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BaseOccupancy = table.Column<int>(type: "integer", nullable: false),
                    MaxOccupancy = table.Column<int>(type: "integer", nullable: false),
                    BasePricePerNight = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Amenities = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_types", x => x.Id);
                    table.CheckConstraint("CK_room_types_base_price_positive", "\"BasePricePerNight\" > 0");
                    table.CheckConstraint("CK_room_types_occupancy", "\"BaseOccupancy\" >= 1 AND \"MaxOccupancy\" >= \"BaseOccupancy\" AND \"MaxOccupancy\" <= 50");
                });

            // Preserve every existing physical room, booking, and quote while
            // introducing sellable room-type inventory. One legacy type per
            // category keeps the migration deterministic and reversible.
            migrationBuilder.Sql(
                """
                INSERT INTO room_types
                    ("Id", "Code", "Name", "Category", "BaseOccupancy", "MaxOccupancy",
                     "BasePricePerNight", "Description", "Amenities", "IsActive",
                     "CreatedAtUtc", "UpdatedAtUtc")
                SELECT
                    md5('moore-room-type:' || "Category")::uuid,
                    LEFT('LEGACY-' || UPPER("Category"), 30),
                    "Category" || ' Room',
                    "Category",
                    1,
                    GREATEST(MAX("Capacity"), 1),
                    GREATEST(MIN("PricePerNight"), 0.01),
                    'Migrated room type. Review its name, occupancy, amenities, and base rate before launch.',
                    '[]'::jsonb,
                    TRUE,
                    NOW(),
                    NOW()
                FROM rooms
                GROUP BY "Category";

                UPDATE rooms
                SET "RoomTypeId" = md5('moore-room-type:' || "Category")::uuid;

                UPDATE bookings AS booking
                SET "RoomTypeId" = room."RoomTypeId",
                    "RoomQuantity" = 1
                FROM rooms AS room
                WHERE room."Id" = booking."RoomId";

                UPDATE booking_quotes AS quote
                SET "RoomTypeId" = room."RoomTypeId",
                    "RoomQuantity" = 1
                FROM rooms AS room
                WHERE room."Id" = quote."RoomId";
                """);

            migrationBuilder.CreateTable(
                name: "folio_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FolioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ExternalReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ReversesEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folio_entries", x => x.Id);
                    table.CheckConstraint("CK_folio_entries_amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_folio_entries_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_folio_entries_direction", "(\"Type\" IN ('RoomCharge','AddOnCharge','Tax','Fee','Refund') AND \"Direction\" = 'Debit') OR (\"Type\" IN ('Discount','Payment','Credit') AND \"Direction\" = 'Credit') OR \"Type\" IN ('Adjustment','Void')");
                    table.CheckConstraint("CK_folio_entries_void_reference", "(\"Type\" = 'Void' AND \"ReversesEntryId\" IS NOT NULL) OR (\"Type\" <> 'Void' AND \"ReversesEntryId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_folio_entries_folio_entries_ReversesEntryId",
                        column: x => x.ReversesEntryId,
                        principalTable: "folio_entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_folio_entries_folios_FolioId",
                        column: x => x.FolioId,
                        principalTable: "folios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_folio_entries_users_PostedByUserId",
                        column: x => x.PostedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "reservation_rooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomTypeCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RoomTypeName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AssignedRoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservation_rooms", x => x.Id);
                    table.CheckConstraint("CK_reservation_rooms_sequence", "\"Sequence\" BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_reservation_rooms_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_reservation_rooms_room_types_RoomTypeId",
                        column: x => x.RoomTypeId,
                        principalTable: "room_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reservation_rooms_rooms_AssignedRoomId",
                        column: x => x.AssignedRoomId,
                        principalTable: "rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reservation_rooms_users_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "room_inventory_closures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Units = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_inventory_closures", x => x.Id);
                    table.CheckConstraint("CK_room_inventory_closures_dates", "\"EndDate\" > \"StartDate\"");
                    table.CheckConstraint("CK_room_inventory_closures_units", "\"Units\" > 0 AND (\"RoomId\" IS NULL OR \"Units\" = 1)");
                    table.ForeignKey(
                        name: "FK_room_inventory_closures_room_types_RoomTypeId",
                        column: x => x.RoomTypeId,
                        principalTable: "room_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_room_inventory_closures_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_room_inventory_closures_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Backfill one reservation unit and an append-only folio for every
            // historical booking. The room-charge calculation subtracts known
            // add-ons from Amount so older rows whose RoomSubtotal was populated
            // from Amount are not charged twice.
            migrationBuilder.Sql(
                """
                INSERT INTO reservation_rooms
                    ("Id", "BookingId", "RoomTypeId", "RoomTypeCode", "RoomTypeName",
                     "AssignedRoomId", "Sequence", "CreatedAtUtc", "AssignedAtUtc", "AssignedByUserId")
                SELECT
                    md5('reservation-room:' || booking."Id"::text)::uuid,
                    booking."Id",
                    booking."RoomTypeId",
                    room_type."Code",
                    room_type."Name",
                    booking."RoomId",
                    1,
                    booking."CreatedAt",
                    booking."CreatedAt",
                    NULL
                FROM bookings AS booking
                JOIN room_types AS room_type ON room_type."Id" = booking."RoomTypeId";

                INSERT INTO folios
                    ("Id", "BookingId", "Currency", "Status", "OpenedAtUtc", "ClosedAtUtc", "ClosedByUserId")
                SELECT
                    md5('folio:' || booking."Id"::text)::uuid,
                    booking."Id",
                    booking."Currency",
                    'Open',
                    booking."CreatedAt",
                    NULL,
                    NULL
                FROM bookings AS booking;

                INSERT INTO folio_entries
                    ("Id", "FolioId", "Type", "Direction", "Amount", "Currency", "Description",
                     "SourceType", "SourceId", "ExternalReference", "IdempotencyKey",
                     "ReversesEntryId", "PostedAtUtc", "PostedByUserId", "Notes")
                SELECT
                    md5('folio-room:' || booking."Id"::text)::uuid,
                    md5('folio:' || booking."Id"::text)::uuid,
                    'RoomCharge', 'Debit',
                    GREATEST(
                        booking."Amount" - COALESCE(addons.total, 0) + booking."DiscountAmount" -
                        booking."TaxAmount" - booking."FeeAmount",
                        0.01),
                    booking."Currency", 'Room accommodation', 'Booking', booking."Id"::text,
                    NULL, 'migration:room:' || booking."Id"::text,
                    NULL, booking."CreatedAt", NULL, 'Backfilled from the historical booking total.'
                FROM bookings AS booking
                LEFT JOIN (
                    SELECT "BookingId", SUM("TotalPrice") AS total
                    FROM booking_addons GROUP BY "BookingId"
                ) AS addons ON addons."BookingId" = booking."Id"
                WHERE booking."Amount" > 0;

                INSERT INTO folio_entries
                    ("Id", "FolioId", "Type", "Direction", "Amount", "Currency", "Description",
                     "SourceType", "SourceId", "ExternalReference", "IdempotencyKey",
                     "ReversesEntryId", "PostedAtUtc", "PostedByUserId", "Notes")
                SELECT md5('folio-discount:' || "Id"::text)::uuid,
                       md5('folio:' || "Id"::text)::uuid,
                       'Discount', 'Credit', "DiscountAmount", "Currency", 'Reservation discount',
                       'Booking', "Id"::text, NULL, 'migration:discount:' || "Id"::text,
                       NULL, "CreatedAt", NULL, NULL
                FROM bookings WHERE "DiscountAmount" > 0;

                INSERT INTO folio_entries
                    ("Id", "FolioId", "Type", "Direction", "Amount", "Currency", "Description",
                     "SourceType", "SourceId", "ExternalReference", "IdempotencyKey",
                     "ReversesEntryId", "PostedAtUtc", "PostedByUserId", "Notes")
                SELECT md5('folio-tax:' || "Id"::text)::uuid,
                       md5('folio:' || "Id"::text)::uuid,
                       'Tax', 'Debit', "TaxAmount", "Currency", 'Exclusive taxes',
                       'Booking', "Id"::text, NULL, 'migration:tax:' || "Id"::text,
                       NULL, "CreatedAt", NULL, NULL
                FROM bookings WHERE "TaxAmount" > 0;

                INSERT INTO folio_entries
                    ("Id", "FolioId", "Type", "Direction", "Amount", "Currency", "Description",
                     "SourceType", "SourceId", "ExternalReference", "IdempotencyKey",
                     "ReversesEntryId", "PostedAtUtc", "PostedByUserId", "Notes")
                SELECT md5('folio-fee:' || "Id"::text)::uuid,
                       md5('folio:' || "Id"::text)::uuid,
                       'Fee', 'Debit', "FeeAmount", "Currency", 'Reservation fees',
                       'Booking', "Id"::text, NULL, 'migration:fee:' || "Id"::text,
                       NULL, "CreatedAt", NULL, NULL
                FROM bookings WHERE "FeeAmount" > 0;

                INSERT INTO folio_entries
                    ("Id", "FolioId", "Type", "Direction", "Amount", "Currency", "Description",
                     "SourceType", "SourceId", "ExternalReference", "IdempotencyKey",
                     "ReversesEntryId", "PostedAtUtc", "PostedByUserId", "Notes")
                SELECT md5('folio-addon:' || addon."Id"::text)::uuid,
                       md5('folio:' || addon."BookingId"::text)::uuid,
                       'AddOnCharge', 'Debit', addon."TotalPrice", booking."Currency",
                       COALESCE(service."Name", 'Historical add-on'), 'BookingAddOn', addon."Id"::text,
                       NULL, 'migration:addon:' || addon."Id"::text,
                       NULL, addon."AddedAtUtc", NULL, addon."Notes"
                FROM booking_addons AS addon
                JOIN bookings AS booking ON booking."Id" = addon."BookingId"
                LEFT JOIN addon_services AS service ON service."Id" = addon."AddOnServiceId";

                INSERT INTO folio_entries
                    ("Id", "FolioId", "Type", "Direction", "Amount", "Currency", "Description",
                     "SourceType", "SourceId", "ExternalReference", "IdempotencyKey",
                     "ReversesEntryId", "PostedAtUtc", "PostedByUserId", "Notes")
                SELECT md5('folio-payment:' || booking."Id"::text)::uuid,
                       md5('folio:' || booking."Id"::text)::uuid,
                       'Payment', 'Credit',
                       GREATEST(COALESCE(transaction."Amount", booking."Amount"), 0.01),
                       booking."Currency", 'Historical verified payment', 'Migration', booking."Id"::text,
                       NULL, 'migration:payment:' || booking."Id"::text,
                       NULL, COALESCE(booking."PaymentConfirmedAtUtc", booking."CreatedAt"),
                       booking."PaymentConfirmedByUserId", NULL
                FROM bookings AS booking
                LEFT JOIN monnify_transactions AS transaction ON transaction."BookingId" = booking."Id"
                WHERE booking."PaymentStatus" IN ('Paid', 'RefundPending', 'Refunded')
                   OR transaction."Status" IN ('PAID', 'OVERPAID', 'PAID_AFTER_EXPIRY');

                INSERT INTO folio_entries
                    ("Id", "FolioId", "Type", "Direction", "Amount", "Currency", "Description",
                     "SourceType", "SourceId", "ExternalReference", "IdempotencyKey",
                     "ReversesEntryId", "PostedAtUtc", "PostedByUserId", "Notes")
                SELECT md5('folio-cancel:' || "Id"::text)::uuid,
                       md5('folio:' || "Id"::text)::uuid,
                       'Credit', 'Credit', "Amount", "Currency", 'Reservation cancellation credit',
                       'Cancellation', "Id"::text, NULL, 'migration:cancel:' || "Id"::text,
                       NULL, COALESCE("CancelledAtUtc", "CreatedAt"), NULL, NULL
                FROM bookings WHERE "Status" = 'Cancelled' AND "Amount" > 0;

                INSERT INTO folio_entries
                    ("Id", "FolioId", "Type", "Direction", "Amount", "Currency", "Description",
                     "SourceType", "SourceId", "ExternalReference", "IdempotencyKey",
                     "ReversesEntryId", "PostedAtUtc", "PostedByUserId", "Notes")
                SELECT md5('folio-refund:' || "Id"::text)::uuid,
                       md5('folio:' || "Id"::text)::uuid,
                       'Refund', 'Debit', "RefundAmount", "Currency", 'Historical refund',
                       'Refund', "Id"::text, NULL, 'migration:refund:' || "Id"::text,
                       NULL, COALESCE("RefundProcessedAtUtc", "CreatedAt"),
                       "RefundProcessedByUserId", "RefundNotes"
                FROM bookings WHERE "RefundAmount" > 0;

                UPDATE bookings AS booking
                SET "PaymentStatus" = 'RefundPending'
                FROM monnify_transactions AS transaction
                WHERE transaction."BookingId" = booking."Id"
                  AND transaction."Status" = 'PAID_AFTER_EXPIRY'
                  AND booking."Status" = 'Cancelled';
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomTypeId",
                table: "rooms",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomTypeId",
                table: "bookings",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomTypeId",
                table: "booking_quotes",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION enforce_booking_room_capacity()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    maximum_occupancy integer;
                BEGIN
                    SELECT "MaxOccupancy" * NEW."RoomQuantity"
                    INTO maximum_occupancy
                    FROM room_types
                    WHERE "Id" = NEW."RoomTypeId";

                    IF maximum_occupancy IS NOT NULL
                       AND NEW."AdultCount" + NEW."ChildCount" > maximum_occupancy THEN
                        RAISE EXCEPTION 'Booking occupancy exceeds reserved room-type capacity.'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'CK_bookings_room_capacity';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                DROP TRIGGER IF EXISTS "TR_bookings_room_capacity" ON bookings;
                CREATE TRIGGER "TR_bookings_room_capacity"
                BEFORE INSERT OR UPDATE OF "RoomTypeId", "RoomQuantity", "AdultCount", "ChildCount"
                ON bookings
                FOR EACH ROW
                EXECUTE FUNCTION enforce_booking_room_capacity();
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION prevent_folio_entry_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Folio entries are append-only; post a reversing entry.'
                        USING ERRCODE = '23514',
                              CONSTRAINT = 'CK_folio_entries_immutable';
                END;
                $$;

                CREATE TRIGGER "TR_folio_entries_immutable"
                BEFORE UPDATE OR DELETE ON folio_entries
                FOR EACH ROW
                EXECUTE FUNCTION prevent_folio_entry_mutation();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_rooms_RoomTypeId",
                table: "rooms",
                column: "RoomTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_daily_room_rates_RatePlanId_RoomTypeId_StayDate",
                table: "daily_room_rates",
                columns: new[] { "RatePlanId", "RoomTypeId", "StayDate" },
                unique: true,
                filter: "\"RoomTypeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_daily_room_rates_RoomTypeId",
                table: "daily_room_rates",
                column: "RoomTypeId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_daily_room_rates_scope",
                table: "daily_room_rates",
                sql: "(CASE WHEN \"RoomId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"RoomTypeId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"RoomCategory\" IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_RoomTypeId_CheckIn_CheckOut_Status",
                table: "bookings",
                columns: new[] { "RoomTypeId", "CheckIn", "CheckOut", "Status" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_bookings_room_quantity",
                table: "bookings",
                sql: "\"RoomQuantity\" BETWEEN 1 AND 10");

            migrationBuilder.CreateIndex(
                name: "IX_booking_quotes_RoomTypeId",
                table: "booking_quotes",
                column: "RoomTypeId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_booking_quotes_room_quantity",
                table: "booking_quotes",
                sql: "\"RoomQuantity\" BETWEEN 1 AND 10");

            migrationBuilder.CreateIndex(
                name: "IX_folio_entries_ExternalReference",
                table: "folio_entries",
                column: "ExternalReference",
                unique: true,
                filter: "\"ExternalReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_folio_entries_FolioId_PostedAtUtc_Id",
                table: "folio_entries",
                columns: new[] { "FolioId", "PostedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_folio_entries_IdempotencyKey",
                table: "folio_entries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_folio_entries_PostedByUserId",
                table: "folio_entries",
                column: "PostedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_folio_entries_ReversesEntryId",
                table: "folio_entries",
                column: "ReversesEntryId",
                unique: true,
                filter: "\"ReversesEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_folios_BookingId",
                table: "folios",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_folios_ClosedByUserId",
                table: "folios",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_rooms_AssignedByUserId",
                table: "reservation_rooms",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_rooms_AssignedRoomId_BookingId",
                table: "reservation_rooms",
                columns: new[] { "AssignedRoomId", "BookingId" },
                filter: "\"AssignedRoomId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_rooms_BookingId_Sequence",
                table: "reservation_rooms",
                columns: new[] { "BookingId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reservation_rooms_RoomTypeId_BookingId",
                table: "reservation_rooms",
                columns: new[] { "RoomTypeId", "BookingId" });

            migrationBuilder.CreateIndex(
                name: "IX_room_inventory_closures_CreatedByUserId",
                table: "room_inventory_closures",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_room_inventory_closures_RoomId_StartDate_EndDate",
                table: "room_inventory_closures",
                columns: new[] { "RoomId", "StartDate", "EndDate" },
                filter: "\"RoomId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_room_inventory_closures_RoomTypeId_StartDate_EndDate_IsActi~",
                table: "room_inventory_closures",
                columns: new[] { "RoomTypeId", "StartDate", "EndDate", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_room_types_Code",
                table: "room_types",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_room_types_IsActive_Category_MaxOccupancy",
                table: "room_types",
                columns: new[] { "IsActive", "Category", "MaxOccupancy" });

            migrationBuilder.AddForeignKey(
                name: "FK_booking_quotes_room_types_RoomTypeId",
                table: "booking_quotes",
                column: "RoomTypeId",
                principalTable: "room_types",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_bookings_room_types_RoomTypeId",
                table: "bookings",
                column: "RoomTypeId",
                principalTable: "room_types",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_daily_room_rates_room_types_RoomTypeId",
                table: "daily_room_rates",
                column: "RoomTypeId",
                principalTable: "room_types",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_rooms_room_types_RoomTypeId",
                table: "rooms",
                column: "RoomTypeId",
                principalTable: "room_types",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_folio_entries_immutable" ON folio_entries;
                DROP FUNCTION IF EXISTS prevent_folio_entry_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_booking_quotes_room_types_RoomTypeId",
                table: "booking_quotes");

            migrationBuilder.DropForeignKey(
                name: "FK_bookings_room_types_RoomTypeId",
                table: "bookings");

            migrationBuilder.DropForeignKey(
                name: "FK_daily_room_rates_room_types_RoomTypeId",
                table: "daily_room_rates");

            migrationBuilder.DropForeignKey(
                name: "FK_rooms_room_types_RoomTypeId",
                table: "rooms");

            migrationBuilder.DropTable(
                name: "folio_entries");

            migrationBuilder.DropTable(
                name: "reservation_rooms");

            migrationBuilder.DropTable(
                name: "room_inventory_closures");

            migrationBuilder.DropTable(
                name: "folios");

            migrationBuilder.DropTable(
                name: "room_types");

            migrationBuilder.DropIndex(
                name: "IX_rooms_RoomTypeId",
                table: "rooms");

            migrationBuilder.DropIndex(
                name: "IX_daily_room_rates_RatePlanId_RoomTypeId_StayDate",
                table: "daily_room_rates");

            migrationBuilder.DropIndex(
                name: "IX_daily_room_rates_RoomTypeId",
                table: "daily_room_rates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_daily_room_rates_scope",
                table: "daily_room_rates");

            migrationBuilder.DropIndex(
                name: "IX_bookings_RoomTypeId_CheckIn_CheckOut_Status",
                table: "bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bookings_room_quantity",
                table: "bookings");

            migrationBuilder.DropIndex(
                name: "IX_booking_quotes_RoomTypeId",
                table: "booking_quotes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_booking_quotes_room_quantity",
                table: "booking_quotes");

            migrationBuilder.DropColumn(
                name: "RoomTypeId",
                table: "rooms");

            migrationBuilder.DropColumn(
                name: "RoomTypeId",
                table: "daily_room_rates");

            migrationBuilder.DropColumn(
                name: "RefundApprovedAmount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RoomQuantity",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RoomTypeId",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RoomQuantity",
                table: "booking_quotes");

            migrationBuilder.DropColumn(
                name: "RoomTypeId",
                table: "booking_quotes");

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomId",
                table: "bookings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomId",
                table: "booking_quotes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_daily_room_rates_scope",
                table: "daily_room_rates",
                sql: "(\"RoomId\" IS NOT NULL AND \"RoomCategory\" IS NULL) OR (\"RoomId\" IS NULL AND \"RoomCategory\" IS NOT NULL)");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION enforce_booking_room_capacity()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    room_capacity integer;
                BEGIN
                    SELECT "Capacity"
                    INTO room_capacity
                    FROM rooms
                    WHERE "Id" = NEW."RoomId";

                    IF room_capacity IS NOT NULL
                       AND NEW."AdultCount" + NEW."ChildCount" > room_capacity THEN
                        RAISE EXCEPTION 'Booking occupancy exceeds room capacity.'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'CK_bookings_room_capacity';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                DROP TRIGGER IF EXISTS "TR_bookings_room_capacity" ON bookings;
                CREATE TRIGGER "TR_bookings_room_capacity"
                BEFORE INSERT OR UPDATE OF "RoomId", "AdultCount", "ChildCount"
                ON bookings
                FOR EACH ROW
                EXECUTE FUNCTION enforce_booking_room_capacity();
                """);
        }
    }
}
