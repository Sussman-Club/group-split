using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddingUserToRuleUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "PercentRuleUser",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_PercentRuleUser_UserId",
                table: "PercentRuleUser",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_PercentRuleUser_User_UserId",
                table: "PercentRuleUser",
                column: "UserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PercentRuleUser_User_UserId",
                table: "PercentRuleUser");

            migrationBuilder.DropIndex(
                name: "IX_PercentRuleUser_UserId",
                table: "PercentRuleUser");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "PercentRuleUser");
        }
    }
}
