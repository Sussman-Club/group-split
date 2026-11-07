using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptMerchant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                table: "Receipt",
                type: "uuid",
                nullable: true);

            // Backfill only places the receipt owner has already used on an expense.
            migrationBuilder.Sql("""
                UPDATE "Receipt" AS r
                SET "MerchantId" = m."Id"
                FROM "Merchant" AS m
                WHERE r."OwnerUserId" IS NOT NULL AND r."MerchantName" IS NOT NULL
                  AND lower(btrim(r."MerchantName")) = m."NormalizedName"
                  AND EXISTS (SELECT 1 FROM "Transaction" AS t
                              WHERE t."UserId" = r."OwnerUserId" AND t."MerchantId" = m."Id");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Receipt_MerchantId",
                table: "Receipt",
                column: "MerchantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Receipt_Merchant_MerchantId",
                table: "Receipt",
                column: "MerchantId",
                principalTable: "Merchant",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Receipt_Merchant_MerchantId",
                table: "Receipt");

            migrationBuilder.DropIndex(
                name: "IX_Receipt_MerchantId",
                table: "Receipt");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                table: "Receipt");
        }
    }
}
