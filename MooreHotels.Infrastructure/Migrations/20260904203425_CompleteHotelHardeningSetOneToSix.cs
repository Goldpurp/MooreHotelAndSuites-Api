using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteHotelHardeningSetOneToSix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PrivacyPolicyAcceptedAtUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrivacyPolicyVersion",
                table: "users",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnonymizedAtUtc",
                table: "guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdultCount",
                table: "bookings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "BookingTermsVersion",
                table: "bookings",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ChildCount",
                table: "bookings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PoliciesAcceptedAtUtc",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrivacyPolicyVersion",
                table: "bookings",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "privacy_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_privacy_requests_guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_privacy_requests_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_privacy_requests_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_guests_AnonymizedAtUtc",
                table: "guests",
                column: "AnonymizedAtUtc");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bookings_occupancy_counts",
                table: "bookings",
                sql: "\"AdultCount\" >= 1 AND \"ChildCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_users_privacy_acceptance_consistent",
                table: "users",
                sql: "(\"PrivacyPolicyVersion\" IS NULL AND \"PrivacyPolicyAcceptedAtUtc\" IS NULL) OR (\"PrivacyPolicyVersion\" IS NOT NULL AND \"PrivacyPolicyAcceptedAtUtc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bookings_policy_acceptance_consistent",
                table: "bookings",
                sql: "(\"PrivacyPolicyVersion\" IS NULL AND \"BookingTermsVersion\" IS NULL AND \"PoliciesAcceptedAtUtc\" IS NULL) OR (\"PrivacyPolicyVersion\" IS NOT NULL AND \"BookingTermsVersion\" IS NOT NULL AND \"PoliciesAcceptedAtUtc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_privacy_requests_resolution_consistent",
                table: "privacy_requests",
                sql: "(\"Status\" IN ('Completed', 'Rejected') AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL) OR (\"Status\" IN ('Pending', 'InProgress') AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL)");

            migrationBuilder.Sql("""
                CREATE FUNCTION enforce_booking_room_capacity()
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

                CREATE TRIGGER "TR_bookings_room_capacity"
                BEFORE INSERT OR UPDATE OF "RoomId", "AdultCount", "ChildCount"
                ON bookings
                FOR EACH ROW
                EXECUTE FUNCTION enforce_booking_room_capacity();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_GuestId_RequestedAtUtc",
                table: "privacy_requests",
                columns: new[] { "GuestId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_RequestedByUserId",
                table: "privacy_requests",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_ResolvedByUserId",
                table: "privacy_requests",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_Status_RequestedAtUtc",
                table: "privacy_requests",
                columns: new[] { "Status", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_bookings_room_capacity" ON bookings;
                DROP FUNCTION IF EXISTS enforce_booking_room_capacity();
                """);

            migrationBuilder.DropTable(
                name: "privacy_requests");

            migrationBuilder.DropIndex(
                name: "IX_guests_AnonymizedAtUtc",
                table: "guests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bookings_occupancy_counts",
                table: "bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_users_privacy_acceptance_consistent",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bookings_policy_acceptance_consistent",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "PrivacyPolicyAcceptedAtUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PrivacyPolicyVersion",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AnonymizedAtUtc",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "AdultCount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "BookingTermsVersion",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "ChildCount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "PoliciesAcceptedAtUtc",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "PrivacyPolicyVersion",
                table: "bookings");
        }
    }
}
