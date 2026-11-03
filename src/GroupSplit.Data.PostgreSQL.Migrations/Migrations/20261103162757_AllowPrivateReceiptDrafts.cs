using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AllowPrivateReceiptDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ExpenseId",
                table: "Receipt",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Receipt",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipt_OwnerUserId",
                table: "Receipt",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Receipt_User_OwnerUserId",
                table: "Receipt",
                column: "OwnerUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"Receipt\" WHERE \"ExpenseId\" IS NULL) THEN RAISE EXCEPTION 'File or delete private receipt drafts before rolling back.'; END IF; END $$;");
            migrationBuilder.DropForeignKey(
                name: "FK_Receipt_User_OwnerUserId",
                table: "Receipt");

            migrationBuilder.DropIndex(
                name: "IX_Receipt_OwnerUserId",
                table: "Receipt");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Receipt");

            migrationBuilder.AlterColumn<Guid>(
                name: "ExpenseId",
                table: "Receipt",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
