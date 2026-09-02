using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlementIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rule_GroupId",
                table: "Rule");

            migrationBuilder.CreateIndex(
                name: "IX_Rule_Category",
                table: "Rule",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Rule_GroupId_Category",
                table: "Rule",
                columns: new[] { "GroupId", "Category" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rule_Category",
                table: "Rule");

            migrationBuilder.DropIndex(
                name: "IX_Rule_GroupId_Category",
                table: "Rule");

            migrationBuilder.CreateIndex(
                name: "IX_Rule_GroupId",
                table: "Rule",
                column: "GroupId");
        }
    }
}
