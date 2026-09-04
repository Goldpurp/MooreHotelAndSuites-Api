using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnServicesAndBookingAddOns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "addon_services",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_addon_services", x => x.Id);
                    table.CheckConstraint("CK_addon_services_price_positive", "\"Price\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "booking_addons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddOnServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AddedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_addons", x => x.Id);
                    table.CheckConstraint("CK_booking_addons_quantity_positive", "\"Quantity\" > 0");
                    table.CheckConstraint("CK_booking_addons_total_matches_quantity", "\"TotalPrice\" = \"UnitPrice\" * \"Quantity\"");
                    table.CheckConstraint("CK_booking_addons_unit_price_positive", "\"UnitPrice\" > 0");
                    table.ForeignKey(
                        name: "FK_booking_addons_addon_services_AddOnServiceId",
                        column: x => x.AddOnServiceId,
                        principalTable: "addon_services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_booking_addons_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_addon_services_IsActive_Category",
                table: "addon_services",
                columns: new[] { "IsActive", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_addons_AddOnServiceId",
                table: "booking_addons",
                column: "AddOnServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_booking_addons_BookingId_AddedAtUtc",
                table: "booking_addons",
                columns: new[] { "BookingId", "AddedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_addons");

            migrationBuilder.DropTable(
                name: "addon_services");
        }
    }
}
