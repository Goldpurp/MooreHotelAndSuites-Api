using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BindAmendmentQuotesToReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AmendmentBookingId",
                table: "booking_quotes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_quotes_AmendmentBookingId",
                table: "booking_quotes",
                column: "AmendmentBookingId");

            migrationBuilder.AddForeignKey(
                name: "FK_booking_quotes_bookings_AmendmentBookingId",
                table: "booking_quotes",
                column: "AmendmentBookingId",
                principalTable: "bookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_booking_quotes_bookings_AmendmentBookingId",
                table: "booking_quotes");

            migrationBuilder.DropIndex(
                name: "IX_booking_quotes_AmendmentBookingId",
                table: "booking_quotes");

            migrationBuilder.DropColumn(
                name: "AmendmentBookingId",
                table: "booking_quotes");
        }
    }
}
