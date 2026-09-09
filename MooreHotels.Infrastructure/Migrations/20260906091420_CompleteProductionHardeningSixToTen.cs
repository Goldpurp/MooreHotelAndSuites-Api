using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteProductionHardeningSixToTen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUnderLegalHold",
                table: "guests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LegalHoldPlacedAtUtc",
                table: "guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LegalHoldPlacedByUserId",
                table: "guests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalHoldReason",
                table: "guests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryFailureMetadataJson",
                table: "email_outbox",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuarantinedAtUtc",
                table: "email_outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_guests_IsUnderLegalHold",
                table: "guests",
                column: "IsUnderLegalHold");

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_QuarantinedAtUtc",
                table: "email_outbox",
                column: "QuarantinedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_guests_IsUnderLegalHold",
                table: "guests");

            migrationBuilder.DropIndex(
                name: "IX_email_outbox_QuarantinedAtUtc",
                table: "email_outbox");

            migrationBuilder.DropColumn(
                name: "IsUnderLegalHold",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "LegalHoldPlacedAtUtc",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "LegalHoldPlacedByUserId",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "LegalHoldReason",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "DeliveryFailureMetadataJson",
                table: "email_outbox");

            migrationBuilder.DropColumn(
                name: "QuarantinedAtUtc",
                table: "email_outbox");
        }
    }
}
