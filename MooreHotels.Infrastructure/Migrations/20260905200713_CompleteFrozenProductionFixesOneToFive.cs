using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteFrozenProductionFixesOneToFive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DueAtUtc",
                table: "privacy_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExportGeneratedAtUtc",
                table: "privacy_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FulfilledAtUtc",
                table: "privacy_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FulfillmentDigest",
                table: "privacy_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FulfillmentEvidenceReference",
                table: "privacy_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityVerificationReference",
                table: "privacy_requests",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityVerifiedAtUtc",
                table: "privacy_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IdentityVerifiedByUserId",
                table: "privacy_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MarketingObjectedAtUtc",
                table: "guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessingRestrictedAtUtc",
                table: "guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "room_inventory_periods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_inventory_periods", x => x.Id);
                    table.CheckConstraint("CK_room_inventory_periods_dates", "\"EndDate\" IS NULL OR \"EndDate\" > \"StartDate\"");
                    table.ForeignKey(
                        name: "FK_room_inventory_periods_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                UPDATE privacy_requests
                SET "DueAtUtc" = "RequestedAtUtc" + INTERVAL '30 days';

                WITH ranked_completed AS (
                    SELECT p."Id",
                           p."GuestId",
                           p."Type",
                           ROW_NUMBER() OVER (
                               PARTITION BY p."GuestId", p."Type"
                               ORDER BY p."RequestedAtUtc" DESC, p."Id") AS completion_rank
                    FROM privacy_requests AS p
                    WHERE p."Status" = 'Completed'
                )
                UPDATE privacy_requests AS p
                SET "Status" = 'Rejected',
                    "ResolutionNotes" = LEFT(CONCAT(
                        'Legacy completion superseded during evidence migration. ',
                        COALESCE(p."ResolutionNotes", '')), 2000)
                FROM ranked_completed AS ranked
                WHERE p."Id" = ranked."Id"
                  AND (ranked.completion_rank > 1 OR EXISTS (
                      SELECT 1
                      FROM privacy_requests AS open_request
                      WHERE open_request."GuestId" = ranked."GuestId"
                        AND open_request."Type" = ranked."Type"
                        AND open_request."Status" IN ('Pending', 'InProgress')));

                UPDATE privacy_requests
                SET "Status" = 'InProgress',
                    "ResolvedAtUtc" = NULL,
                    "ResolvedByUserId" = NULL,
                    "ResolutionNotes" = LEFT(CONCAT(
                        'Legacy completion reopened because type-specific fulfillment evidence was not recorded. ',
                        COALESCE("ResolutionNotes", '')), 2000)
                WHERE "Status" = 'Completed';

                INSERT INTO room_inventory_periods
                    ("Id", "RoomId", "StartDate", "EndDate", "RecordedAtUtc")
                SELECT gen_random_uuid(),
                       r."Id",
                       (r."CreatedAt" AT TIME ZONE 'Africa/Lagos')::date,
                       CASE WHEN r."IsOnline" THEN NULL
                            ELSE (CURRENT_TIMESTAMP AT TIME ZONE 'Africa/Lagos')::date END,
                       CURRENT_TIMESTAMP
                FROM rooms AS r
                WHERE r."IsOnline"
                   OR (r."CreatedAt" AT TIME ZONE 'Africa/Lagos')::date <
                      (CURRENT_TIMESTAMP AT TIME ZONE 'Africa/Lagos')::date;
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "DueAtUtc",
                table: "privacy_requests",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_GuestId_Type",
                table: "privacy_requests",
                columns: new[] { "GuestId", "Type" },
                unique: true,
                filter: "\"Status\" IN ('Pending', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_IdentityVerifiedByUserId",
                table: "privacy_requests",
                column: "IdentityVerifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_Status_DueAtUtc",
                table: "privacy_requests",
                columns: new[] { "Status", "DueAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_privacy_requests_due_date",
                table: "privacy_requests",
                sql: "\"DueAtUtc\" >= \"RequestedAtUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_privacy_requests_fulfillment_consistent",
                table: "privacy_requests",
                sql: "(\"Status\" = 'Completed' AND \"FulfilledAtUtc\" IS NOT NULL AND \"FulfillmentEvidenceReference\" IS NOT NULL AND \"FulfillmentDigest\" IS NOT NULL) OR (\"Status\" <> 'Completed' AND \"FulfilledAtUtc\" IS NULL AND \"FulfillmentEvidenceReference\" IS NULL AND \"FulfillmentDigest\" IS NULL AND \"ExportGeneratedAtUtc\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_privacy_requests_identity_consistent",
                table: "privacy_requests",
                sql: "(\"IdentityVerifiedAtUtc\" IS NULL AND \"IdentityVerifiedByUserId\" IS NULL AND \"IdentityVerificationReference\" IS NULL) OR (\"IdentityVerifiedAtUtc\" IS NOT NULL AND \"IdentityVerifiedByUserId\" IS NOT NULL AND \"IdentityVerificationReference\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_room_inventory_periods_RoomId",
                table: "room_inventory_periods",
                column: "RoomId",
                unique: true,
                filter: "\"EndDate\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_room_inventory_periods_RoomId_StartDate_EndDate",
                table: "room_inventory_periods",
                columns: new[] { "RoomId", "StartDate", "EndDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_privacy_requests_users_IdentityVerifiedByUserId",
                table: "privacy_requests",
                column: "IdentityVerifiedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_privacy_requests_users_IdentityVerifiedByUserId",
                table: "privacy_requests");

            migrationBuilder.DropTable(
                name: "room_inventory_periods");

            migrationBuilder.DropIndex(
                name: "IX_privacy_requests_GuestId_Type",
                table: "privacy_requests");

            migrationBuilder.DropIndex(
                name: "IX_privacy_requests_IdentityVerifiedByUserId",
                table: "privacy_requests");

            migrationBuilder.DropIndex(
                name: "IX_privacy_requests_Status_DueAtUtc",
                table: "privacy_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_privacy_requests_due_date",
                table: "privacy_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_privacy_requests_fulfillment_consistent",
                table: "privacy_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_privacy_requests_identity_consistent",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "DueAtUtc",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "ExportGeneratedAtUtc",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "FulfilledAtUtc",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "FulfillmentDigest",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "FulfillmentEvidenceReference",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "IdentityVerificationReference",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "IdentityVerifiedAtUtc",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "IdentityVerifiedByUserId",
                table: "privacy_requests");

            migrationBuilder.DropColumn(
                name: "MarketingObjectedAtUtc",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "ProcessingRestrictedAtUtc",
                table: "guests");
        }
    }
}
