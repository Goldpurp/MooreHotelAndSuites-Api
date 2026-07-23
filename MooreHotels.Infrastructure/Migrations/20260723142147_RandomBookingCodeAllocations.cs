using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RandomBookingCodeAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropSequence(
                name: "booking_code_sequence");

            migrationBuilder.CreateTable(
                name: "booking_code_allocations",
                columns: table => new
                {
                    // Match the historical bookings column during backfill.
                    // New allocations are still always the short MHS###### form.
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AllocatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_code_allocations", x => x.Code);
                });

            // Preserve every previously issued reference so a random allocator
            // can never reuse a code that already belongs to a booking.
            migrationBuilder.Sql(
                """
                INSERT INTO booking_code_allocations ("Code", "AllocatedAtUtc")
                SELECT "BookingCode", "CreatedAt"
                FROM bookings
                ON CONFLICT ("Code") DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_booking_code_allocations_AllocatedAtUtc",
                table: "booking_code_allocations",
                column: "AllocatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_code_allocations");

            migrationBuilder.CreateSequence(
                name: "booking_code_sequence");

            // If this migration is rolled back, resume the legacy sequence above
            // every numeric MHS reference that remains in the bookings table.
            migrationBuilder.Sql(
                """
                SELECT setval(
                    'booking_code_sequence',
                    GREATEST(
                        COALESCE(
                            MAX(SUBSTRING("BookingCode" FROM 4)::bigint),
                            0) + 1,
                        1),
                    false)
                FROM bookings
                WHERE "BookingCode" ~ '^MHS[0-9]+$';
                """);
        }
    }
}
