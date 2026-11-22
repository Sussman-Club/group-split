using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class TransactionEnteredByAndSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable until the backfill below has given every row an answer.
            migrationBuilder.AddColumn<Guid>(
                name: "EnteredById",
                table: "Transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Transaction",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Backfilled 2026-11-22. Entered by the payer, which the old rules guaranteed for a
            // repayment and is the likeliest answer for an expense. The source is read off what
            // the row is attached to: a bank row, then a receipt from somebody's receipt inbox
            // (one with an owner), otherwise typed in. Nothing written before this was recorded
            // through MCP in a way the row can show.
            migrationBuilder.Sql("""
                UPDATE "Transaction" AS t
                SET "EnteredById" = t."UserId",
                    "Source" = CASE
                        WHEN t."BankTransactionId" IS NOT NULL THEN 'Bank'
                        WHEN EXISTS (SELECT 1 FROM "Receipt" AS r
                                     WHERE r."ExpenseId" = t."Id" AND r."OwnerUserId" IS NOT NULL) THEN 'Receipt'
                        ELSE 'Manual'
                    END;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "EnteredById",
                table: "Transaction",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Source",
                table: "Transaction",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_EnteredById",
                table: "Transaction",
                column: "EnteredById");

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_User_EnteredById",
                table: "Transaction",
                column: "EnteredById",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_User_EnteredById",
                table: "Transaction");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_EnteredById",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "EnteredById",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Transaction");
        }
    }
}
