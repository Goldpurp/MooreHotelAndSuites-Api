using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SecureMonnifyPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BookingId",
                table: "monnify_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "monnify_transactions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAtUtc",
                table: "monnify_transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentProviderReference",
                table: "bookings",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_monnify_transactions_BookingId",
                table: "monnify_transactions",
                column: "BookingId",
                unique: true,
                filter: "\"BookingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_PaymentProviderReference",
                table: "bookings",
                column: "PaymentProviderReference",
                unique: true,
                filter: "\"PaymentProviderReference\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_monnify_transactions_bookings_BookingId",
                table: "monnify_transactions",
                column: "BookingId",
                principalTable: "bookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_monnify_transactions_bookings_BookingId",
                table: "monnify_transactions");

            migrationBuilder.DropIndex(
                name: "IX_monnify_transactions_BookingId",
                table: "monnify_transactions");

            migrationBuilder.DropIndex(
                name: "IX_bookings_PaymentProviderReference",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "BookingId",
                table: "monnify_transactions");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "monnify_transactions");

            migrationBuilder.DropColumn(
                name: "VerifiedAtUtc",
                table: "monnify_transactions");

            migrationBuilder.DropColumn(
                name: "PaymentProviderReference",
                table: "bookings");
        }
    }
}
