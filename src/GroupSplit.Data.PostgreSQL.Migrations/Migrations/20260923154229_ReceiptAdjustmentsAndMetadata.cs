using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptAdjustmentsAndMetadata : Migration
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

            migrationBuilder.CreateTable(
                name: "ReceiptAdjustment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CouponCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptAdjustment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceiptAdjustment_Receipt_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "Receipt",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptAdjustment_ReceiptId_Position",
                table: "ReceiptAdjustment",
                columns: new[] { "ReceiptId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceiptAdjustment");

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
