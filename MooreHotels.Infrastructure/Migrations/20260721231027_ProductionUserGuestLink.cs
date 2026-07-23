using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProductionUserGuestLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GuestId",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "users" AS u
                SET "GuestId" = candidate."Id"
                FROM (
                    SELECT lower("Email") AS normalized_email, min("Id") AS "Id"
                    FROM "guests"
                    WHERE "Email" IS NOT NULL
                    GROUP BY lower("Email")
                    HAVING count(*) = 1
                ) AS candidate
                WHERE u."GuestId" IS NULL
                  AND u."Role" = 'Client'
                  AND lower(u."Email") = candidate.normalized_email;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_users_GuestId",
                table: "users",
                column: "GuestId",
                unique: true,
                filter: "\"GuestId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_users_guests_GuestId",
                table: "users",
                column: "GuestId",
                principalTable: "guests",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_guests_GuestId",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_GuestId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "GuestId",
                table: "users");
        }
    }
}
