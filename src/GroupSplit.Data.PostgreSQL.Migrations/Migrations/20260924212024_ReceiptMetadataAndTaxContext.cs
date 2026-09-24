using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptMetadataAndTaxContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTaxable",
                table: "ReceiptItem",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRate",
                table: "ReceiptItem",
                type: "numeric(9,4)",
                precision: 9,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MerchantName",
                table: "Receipt",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PurchaseDate",
                table: "Receipt",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceValues",
                table: "Receipt",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTaxable",
                table: "ReceiptItem");

            migrationBuilder.DropColumn(
                name: "TaxRate",
                table: "ReceiptItem");

            migrationBuilder.DropColumn(
                name: "MerchantName",
                table: "Receipt");

            migrationBuilder.DropColumn(
                name: "PurchaseDate",
                table: "Receipt");

            migrationBuilder.DropColumn(
                name: "SourceValues",
                table: "Receipt");
        }
    }
}
