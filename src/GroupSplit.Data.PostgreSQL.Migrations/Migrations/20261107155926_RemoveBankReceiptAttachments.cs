using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBankReceiptAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stop before removing the only owner of any existing source file.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "ReceiptAttachment"
                               WHERE "BankTransactionId" IS NOT NULL
                                 AND "ExpenseId" IS NULL AND "ReceiptId" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot remove bank receipt attachments: recover pending files first';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ReceiptAttachment_BankTransaction_BankTransactionId",
                table: "ReceiptAttachment");

            migrationBuilder.DropIndex(
                name: "IX_ReceiptAttachment_BankTransactionId",
                table: "ReceiptAttachment");

            migrationBuilder.DropColumn(
                name: "BankTransactionId",
                table: "ReceiptAttachment");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BankTransactionId",
                table: "ReceiptAttachment",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptAttachment_BankTransactionId",
                table: "ReceiptAttachment",
                column: "BankTransactionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptAttachment_BankTransaction_BankTransactionId",
                table: "ReceiptAttachment",
                column: "BankTransactionId",
                principalTable: "BankTransaction",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
