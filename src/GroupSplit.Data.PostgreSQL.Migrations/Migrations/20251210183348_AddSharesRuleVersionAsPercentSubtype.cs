using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSharesRuleVersionAsPercentSubtype : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SharesRuleVersion_RuleVersion_Id",
                table: "SharesRuleVersion");

            migrationBuilder.AddForeignKey(
                name: "FK_SharesRuleVersion_PercentRuleVersion_Id",
                table: "SharesRuleVersion",
                column: "Id",
                principalTable: "PercentRuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SharesRuleVersion_PercentRuleVersion_Id",
                table: "SharesRuleVersion");

            migrationBuilder.AddForeignKey(
                name: "FK_SharesRuleVersion_RuleVersion_Id",
                table: "SharesRuleVersion",
                column: "Id",
                principalTable: "RuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
