using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteProductionReadinessSevenToTen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "bookings",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "NGN");

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FeeAmount",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "IncludedTaxAmount",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "QuoteId",
                table: "bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RoomSubtotal",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Preserve the historical total as the room subtotal for bookings
            // created before immutable quote breakdowns existed.
            migrationBuilder.Sql(
                "UPDATE bookings SET \"RoomSubtotal\" = \"Amount\", \"Currency\" = 'NGN';");

            migrationBuilder.CreateTable(
                name: "pricing_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Calculation = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IsInclusive = table.Column<bool>(type: "boolean", nullable: false),
                    EffectiveFromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveUntilDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_rules", x => x.Id);
                    table.CheckConstraint("CK_pricing_rules_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_pricing_rules_dates", "\"EffectiveUntilDate\" IS NULL OR \"EffectiveFromDate\" IS NULL OR \"EffectiveUntilDate\" >= \"EffectiveFromDate\"");
                    table.CheckConstraint("CK_pricing_rules_inclusive", "NOT \"IsInclusive\" OR (\"Kind\" = 'Tax' AND \"Calculation\" = 'Percentage')");
                    table.CheckConstraint("CK_pricing_rules_value", "\"Value\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "rate_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BaseAdjustmentType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BaseAdjustmentValue = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MinimumNights = table.Column<int>(type: "integer", nullable: false),
                    MaximumNights = table.Column<int>(type: "integer", nullable: false),
                    SellFromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SellUntilDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_plans", x => x.Id);
                    table.CheckConstraint("CK_rate_plans_adjustment", "\"BaseAdjustmentValue\" >= 0 AND (\"BaseAdjustmentType\" <> 'None' OR \"BaseAdjustmentValue\" = 0)");
                    table.CheckConstraint("CK_rate_plans_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_rate_plans_nights", "\"MinimumNights\" >= 1 AND \"MaximumNights\" >= \"MinimumNights\" AND \"MaximumNights\" <= 90");
                    table.CheckConstraint("CK_rate_plans_sell_dates", "\"SellUntilDate\" IS NULL OR \"SellFromDate\" IS NULL OR \"SellUntilDate\" >= \"SellFromDate\"");
                });

            migrationBuilder.InsertData(
                table: "rate_plans",
                columns: new[]
                {
                    "Id", "Code", "Name", "Description", "Currency",
                    "BaseAdjustmentType", "BaseAdjustmentValue", "MinimumNights",
                    "MaximumNights", "SellFromDate", "SellUntilDate", "IsDefault",
                    "IsActive", "CreatedAtUtc", "UpdatedAtUtc"
                },
                values: new object[]
                {
                    new Guid("7d3a75df-b4f4-47b8-93be-dbb90c84757e"),
                    "STANDARD",
                    "Standard Flexible Rate",
                    "Default flexible rate using the room base price unless a daily override exists.",
                    "NGN",
                    "None",
                    0m,
                    1,
                    90,
                    null,
                    null,
                    true,
                    true,
                    new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc)
                });

            migrationBuilder.CreateTable(
                name: "daily_room_rates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RatePlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoomCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    StayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_room_rates", x => x.Id);
                    table.CheckConstraint("CK_daily_room_rates_amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_daily_room_rates_scope", "(\"RoomId\" IS NOT NULL AND \"RoomCategory\" IS NULL) OR (\"RoomId\" IS NULL AND \"RoomCategory\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_daily_room_rates_rate_plans_RatePlanId",
                        column: x => x.RatePlanId,
                        principalTable: "rate_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_daily_room_rates_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "promotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DiscountType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MaximumDiscountAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    MinimumNights = table.Column<int>(type: "integer", nullable: false),
                    RatePlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    ValidFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RedemptionLimit = table.Column<int>(type: "integer", nullable: true),
                    RedemptionCount = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotions", x => x.Id);
                    table.CheckConstraint("CK_promotions_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_promotions_minimum_nights", "\"MinimumNights\" BETWEEN 1 AND 90");
                    table.CheckConstraint("CK_promotions_percentage", "\"DiscountType\" <> 'Percentage' OR \"Value\" <= 100");
                    table.CheckConstraint("CK_promotions_redemptions", "\"RedemptionCount\" >= 0 AND (\"RedemptionLimit\" IS NULL OR \"RedemptionLimit\" > 0)");
                    table.CheckConstraint("CK_promotions_value", "\"Value\" > 0");
                    table.CheckConstraint("CK_promotions_window", "\"ValidUntilUtc\" > \"ValidFromUtc\"");
                    table.ForeignKey(
                        name: "FK_promotions_rate_plans_RatePlanId",
                        column: x => x.RatePlanId,
                        principalTable: "rate_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "booking_quotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccessTokenHash = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    RatePlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromotionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckInDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CheckOutDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AdultCount = table.Column<int>(type: "integer", nullable: false),
                    ChildCount = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RoomSubtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IncludedTaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FeeAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_quotes", x => x.Id);
                    table.CheckConstraint("CK_booking_quotes_currency", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_booking_quotes_dates", "\"CheckOutDate\" > \"CheckInDate\"");
                    table.CheckConstraint("CK_booking_quotes_occupancy", "\"AdultCount\" >= 1 AND \"ChildCount\" >= 0");
                    table.CheckConstraint("CK_booking_quotes_totals", "\"RoomSubtotal\" >= 0 AND \"DiscountAmount\" BETWEEN 0 AND \"RoomSubtotal\" AND \"IncludedTaxAmount\" >= 0 AND \"TaxAmount\" >= 0 AND \"FeeAmount\" >= 0 AND \"TotalAmount\" = \"RoomSubtotal\" - \"DiscountAmount\" + \"TaxAmount\" + \"FeeAmount\"");
                    table.CheckConstraint("CK_booking_quotes_window", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_booking_quotes_promotions_PromotionId",
                        column: x => x.PromotionId,
                        principalTable: "promotions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_booking_quotes_rate_plans_RatePlanId",
                        column: x => x.RatePlanId,
                        principalTable: "rate_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_booking_quotes_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "booking_quote_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingQuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StayDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsInclusive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_quote_lines", x => x.Id);
                    table.CheckConstraint("CK_booking_quote_lines_amount", "\"Amount\" >= 0");
                    table.CheckConstraint("CK_booking_quote_lines_quantity", "\"Quantity\" > 0");
                    table.CheckConstraint("CK_booking_quote_lines_stay_date", "(\"Type\" = 'RoomNight' AND \"StayDate\" IS NOT NULL) OR (\"Type\" <> 'RoomNight' AND \"StayDate\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_booking_quote_lines_booking_quotes_BookingQuoteId",
                        column: x => x.BookingQuoteId,
                        principalTable: "booking_quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_QuoteId",
                table: "bookings",
                column: "QuoteId",
                unique: true,
                filter: "\"QuoteId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bookings_currency",
                table: "bookings",
                sql: "\"Currency\" ~ '^[A-Z]{3}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bookings_price_breakdown",
                table: "bookings",
                sql: "\"Amount\" >= 0 AND \"RoomSubtotal\" >= 0 AND \"DiscountAmount\" >= 0 AND \"IncludedTaxAmount\" >= 0 AND \"TaxAmount\" >= 0 AND \"FeeAmount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_booking_quote_lines_BookingQuoteId_SortOrder_Id",
                table: "booking_quote_lines",
                columns: new[] { "BookingQuoteId", "SortOrder", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_quotes_AccessTokenHash",
                table: "booking_quotes",
                column: "AccessTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_quotes_ExpiresAtUtc_ConsumedAtUtc",
                table: "booking_quotes",
                columns: new[] { "ExpiresAtUtc", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_quotes_PromotionId",
                table: "booking_quotes",
                column: "PromotionId");

            migrationBuilder.CreateIndex(
                name: "IX_booking_quotes_RatePlanId",
                table: "booking_quotes",
                column: "RatePlanId");

            migrationBuilder.CreateIndex(
                name: "IX_booking_quotes_RoomId",
                table: "booking_quotes",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_daily_room_rates_RatePlanId_RoomCategory_StayDate",
                table: "daily_room_rates",
                columns: new[] { "RatePlanId", "RoomCategory", "StayDate" },
                unique: true,
                filter: "\"RoomCategory\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_daily_room_rates_RatePlanId_RoomId_StayDate",
                table: "daily_room_rates",
                columns: new[] { "RatePlanId", "RoomId", "StayDate" },
                unique: true,
                filter: "\"RoomId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_daily_room_rates_RoomId",
                table: "daily_room_rates",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_pricing_rules_Code",
                table: "pricing_rules",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pricing_rules_IsActive_SortOrder",
                table: "pricing_rules",
                columns: new[] { "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_promotions_Code",
                table: "promotions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotions_IsActive_ValidFromUtc_ValidUntilUtc",
                table: "promotions",
                columns: new[] { "IsActive", "ValidFromUtc", "ValidUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_promotions_RatePlanId",
                table: "promotions",
                column: "RatePlanId");

            migrationBuilder.CreateIndex(
                name: "IX_rate_plans_Code",
                table: "rate_plans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rate_plans_IsActive_SellFromDate_SellUntilDate",
                table: "rate_plans",
                columns: new[] { "IsActive", "SellFromDate", "SellUntilDate" });

            migrationBuilder.CreateIndex(
                name: "IX_rate_plans_IsDefault",
                table: "rate_plans",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = TRUE AND \"IsActive\" = TRUE");

            migrationBuilder.AddForeignKey(
                name: "FK_bookings_booking_quotes_QuoteId",
                table: "bookings",
                column: "QuoteId",
                principalTable: "booking_quotes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_bookings_booking_quotes_QuoteId",
                table: "bookings");

            migrationBuilder.DropTable(
                name: "booking_quote_lines");

            migrationBuilder.DropTable(
                name: "daily_room_rates");

            migrationBuilder.DropTable(
                name: "pricing_rules");

            migrationBuilder.DropTable(
                name: "booking_quotes");

            migrationBuilder.DropTable(
                name: "promotions");

            migrationBuilder.DropTable(
                name: "rate_plans");

            migrationBuilder.DropIndex(
                name: "IX_bookings_QuoteId",
                table: "bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bookings_currency",
                table: "bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bookings_price_breakdown",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "FeeAmount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "IncludedTaxAmount",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "QuoteId",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "RoomSubtotal",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "bookings");
        }
    }
}
