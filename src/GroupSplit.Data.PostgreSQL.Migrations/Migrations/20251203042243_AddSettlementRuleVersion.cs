using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlementRuleVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OtherUserId",
                table: "RuleVersion",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuleVersion_OtherUserId",
                table: "RuleVersion",
                column: "OtherUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_RuleVersion_User_OtherUserId",
                table: "RuleVersion",
                column: "OtherUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RuleVersion_User_OtherUserId",
                table: "RuleVersion");

            migrationBuilder.DropIndex(
                name: "IX_RuleVersion_OtherUserId",
                table: "RuleVersion");

            migrationBuilder.DropColumn(
                name: "OtherUserId",
                table: "RuleVersion");
        }
    }
}
