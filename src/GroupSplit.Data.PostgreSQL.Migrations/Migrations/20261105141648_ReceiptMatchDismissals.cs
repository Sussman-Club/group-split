using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptMatchDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReceiptMatchDismissal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpenseId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptMatchDismissal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceiptMatchDismissal_Receipt_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "Receipt",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReceiptMatchDismissal_Transaction_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Transaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptMatchDismissal_ExpenseId",
                table: "ReceiptMatchDismissal",
                column: "ExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptMatchDismissal_ReceiptId_ExpenseId",
                table: "ReceiptMatchDismissal",
                columns: new[] { "ReceiptId", "ExpenseId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceiptMatchDismissal");
        }
    }
}
