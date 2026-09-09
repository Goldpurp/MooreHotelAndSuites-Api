using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenBookingVerificationAndOperationsLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "booking_email_verifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_email_verifications", x => x.Id);
                    table.CheckConstraint("CK_booking_email_verifications_valid_window", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_visit_records_Timestamp_Id",
                table: "visit_records",
                columns: new[] { "Timestamp", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_CancelledAtUtc_Id",
                table: "bookings",
                columns: new[] { "CancelledAtUtc", "Id" },
                filter: "\"Status\" = 'Cancelled' AND \"CancelledAtUtc\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_CreatedAt_Id",
                table: "bookings",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_email_verifications_Email_CreatedAtUtc",
                table: "booking_email_verifications",
                columns: new[] { "Email", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_email_verifications_ExpiresAtUtc_Id",
                table: "booking_email_verifications",
                columns: new[] { "ExpiresAtUtc", "Id" },
                filter: "\"ConsumedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_booking_email_verifications_TokenHash",
                table: "booking_email_verifications",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_email_verifications");

            migrationBuilder.DropIndex(
                name: "IX_visit_records_Timestamp_Id",
                table: "visit_records");

            migrationBuilder.DropIndex(
                name: "IX_bookings_CancelledAtUtc_Id",
                table: "bookings");

            migrationBuilder.DropIndex(
                name: "IX_bookings_CreatedAt_Id",
                table: "bookings");
        }
    }
}
