using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenRemainingProductionControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GuestAccessLinkLastRequestedAtUtc",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GuestAccessTokenExpiresAtUtc",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GuestAccessTokenIssuedAtUtc",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GuestAccessTokenRevokedAtUtc",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundAmount",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundApprovedAtUtc",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RefundApprovedByUserId",
                table: "bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefundChannel",
                table: "bookings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefundEvidenceType",
                table: "bookings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefundNotes",
                table: "bookings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundProcessedAtUtc",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RefundProcessedByUserId",
                table: "bookings",
                type: "uuid",
                nullable: true);

            // Permanent links issued by older releases intentionally fail
            // closed. Guests can rotate them through the non-enumerating
            // access-link endpoint, which issues a two-hour replacement.
            migrationBuilder.Sql(
                """
                UPDATE bookings
                SET "GuestAccessTokenRevokedAtUtc" = CURRENT_TIMESTAMP,
                    "ProtectedGuestAccessToken" = NULL
                WHERE "GuestAccessTokenHash" IS NOT NULL
                """);

            migrationBuilder.DropColumn(
                name: "ProtectedGuestAccessToken",
                table: "bookings");

            migrationBuilder.CreateTable(
                name: "media_deletion_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LockId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_deletion_outbox", x => x.Id);
                    table.CheckConstraint("CK_media_deletion_outbox_attempt_count", "\"AttemptCount\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_GuestAccessTokenExpiresAtUtc_Id",
                table: "bookings",
                columns: new[] { "GuestAccessTokenExpiresAtUtc", "Id" },
                filter: "\"GuestAccessTokenRevokedAtUtc\" IS NULL AND \"GuestAccessTokenExpiresAtUtc\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_RefundApprovedByUserId",
                table: "bookings",
                column: "RefundApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_RefundProcessedByUserId",
                table: "bookings",
                column: "RefundProcessedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_RefundReference",
                table: "bookings",
                column: "RefundReference",
                unique: true,
                filter: "\"RefundReference\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bookings_guest_access_window",
                table: "bookings",
                sql: "\"GuestAccessTokenExpiresAtUtc\" IS NULL OR (\"GuestAccessTokenIssuedAtUtc\" IS NOT NULL AND \"GuestAccessTokenExpiresAtUtc\" > \"GuestAccessTokenIssuedAtUtc\")");

            migrationBuilder.CreateIndex(
                name: "IX_media_deletion_outbox_CreatedAtUtc",
                table: "media_deletion_outbox",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_media_deletion_outbox_NextAttemptAtUtc_LockedUntilUtc_Attem~",
                table: "media_deletion_outbox",
                columns: new[] { "NextAttemptAtUtc", "LockedUntilUtc", "AttemptCount" });

            migrationBuilder.CreateIndex(
                name: "IX_media_deletion_outbox_PublicId",
                table: "media_deletion_outbox",
                column: "PublicId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_bookings_users_RefundApprovedByUserId",
                table: "bookings",
                column: "RefundApprovedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_bookings_users_RefundProcessedByUserId",
                table: "bookings",
                column: "RefundProcessedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtectedGuestAccessToken",
                table: "bookings",
                type: "text",
                nullable: true);

            migrationBuilder.DropForeignKey(
                name: "FK_bookings_users_RefundApprovedByUserId",
                table: "bookings");

            migrationBuilder.DropForeignKey(
                name: "FK_bookings_users_RefundProcessedByUserId",
                table: "bookings");

            migrationBuilder.DropTable(
                name: "media_deletion_outbox");

            migrationBuilder.DropIndex(
                name: "IX_bookings_GuestAccessTokenExpiresAtUtc_Id",
                table: "bookings");

            migrationBuilder.DropIndex(
                name: "IX_bookings_RefundApprovedByUserId",
                table: "bookings");

            migrationBuilder.DropIndex(
                name: "IX_bookings_RefundProcessedByUserId",
                table: "bookings");

            migrationBuilder.DropIndex(
                name: "IX_bookings_RefundReference",
                table: "bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bookings_guest_access_window",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "GuestAccessLinkLastRequestedAtUtc",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "GuestAccessTokenExpiresAtUtc",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "GuestAccessTokenIssuedAtUtc",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "GuestAccessTokenRevokedAtUtc",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RefundAmount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RefundApprovedAtUtc",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RefundApprovedByUserId",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RefundChannel",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RefundEvidenceType",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RefundNotes",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RefundProcessedAtUtc",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RefundProcessedByUserId",
                table: "bookings");
        }
    }
}
