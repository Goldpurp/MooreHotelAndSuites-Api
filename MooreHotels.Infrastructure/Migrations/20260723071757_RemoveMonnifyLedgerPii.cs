using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MooreHotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMonnifyLedgerPii : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerEmail",
                table: "monnify_transactions");

            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "monnify_transactions");

            migrationBuilder.DropColumn(
                name: "MerchantReference",
                table: "monnify_transactions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerEmail",
                table: "monnify_transactions",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "monnify_transactions",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MerchantReference",
                table: "monnify_transactions",
                type: "text",
                nullable: true);
        }
    }
}
