using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddingUniqueIndexToRuleUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PercentRuleUser_UserId",
                table: "PercentRuleUser");

            migrationBuilder.CreateIndex(
                name: "IX_PercentRuleUser_UserId_RuleVersionId",
                table: "PercentRuleUser",
                columns: new[] { "UserId", "RuleVersionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PercentRuleUser_UserId_RuleVersionId",
                table: "PercentRuleUser");

            migrationBuilder.CreateIndex(
                name: "IX_PercentRuleUser_UserId",
                table: "PercentRuleUser",
                column: "UserId");
        }
    }
}
