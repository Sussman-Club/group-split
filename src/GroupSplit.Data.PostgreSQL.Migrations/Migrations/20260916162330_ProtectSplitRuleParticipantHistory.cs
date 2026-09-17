using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ProtectSplitRuleParticipantHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SplitRuleParticipant_User_UserId",
                table: "SplitRuleParticipant");

            migrationBuilder.AddForeignKey(
                name: "FK_SplitRuleParticipant_User_UserId",
                table: "SplitRuleParticipant",
                column: "UserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SplitRuleParticipant_User_UserId",
                table: "SplitRuleParticipant");

            migrationBuilder.AddForeignKey(
                name: "FK_SplitRuleParticipant_User_UserId",
                table: "SplitRuleParticipant",
                column: "UserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
