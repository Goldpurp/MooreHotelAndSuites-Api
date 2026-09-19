using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptionalRoomDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rooms_RoomNumber",
                table: "rooms");

            migrationBuilder.AddColumn<string>(
                name: "RoomName",
                table: "visit_records",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            // Preserve the room label on historic activity before clearing optional details.
            migrationBuilder.Sql("""
                UPDATE visit_records AS v SET "RoomName" = r."Name"
                FROM rooms AS r WHERE v."RoomId" = r."Id";
                UPDATE rooms SET "RoomNumber" = '', "Size" = '';
                CREATE UNIQUE INDEX "IX_rooms_NameInsensitive" ON rooms (lower(btrim("Name")));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_rooms_Name",
                table: "rooms",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rooms_RoomNumber",
                table: "rooms",
                column: "RoomNumber",
                unique: true,
                filter: "\"RoomNumber\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Cleared room numbers cannot be recreated safely. Restore them from a
            // pre-deployment backup before rolling back a populated database.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM rooms WHERE "RoomNumber" = '') THEN
                        RAISE EXCEPTION 'Restore room numbers from backup before rolling back OptionalRoomDetails.';
                    END IF;
                END $$;
                DROP INDEX "IX_rooms_NameInsensitive";
                """);
            migrationBuilder.DropIndex(
                name: "IX_rooms_Name",
                table: "rooms");

            migrationBuilder.DropIndex(
                name: "IX_rooms_RoomNumber",
                table: "rooms");

            migrationBuilder.DropColumn(
                name: "RoomName",
                table: "visit_records");

            migrationBuilder.CreateIndex(
                name: "IX_rooms_RoomNumber",
                table: "rooms",
                column: "RoomNumber",
                unique: true);
        }
    }
}
