using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ManualTransferTypedAcknowledgement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentConfirmationMethod",
                table: "bookings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentConfirmedAtUtc",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PaymentConfirmedByUserId",
                table: "bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_PaymentConfirmedByUserId",
                table: "bookings",
                column: "PaymentConfirmedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_bookings_users_PaymentConfirmedByUserId",
                table: "bookings",
                column: "PaymentConfirmedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_bookings_users_PaymentConfirmedByUserId",
                table: "bookings");

            migrationBuilder.DropIndex(
                name: "IX_bookings_PaymentConfirmedByUserId",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "PaymentConfirmationMethod",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "PaymentConfirmedAtUtc",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "PaymentConfirmedByUserId",
                table: "bookings");
        }
    }
}
