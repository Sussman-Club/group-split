using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ChangeTransactionFromGroupToRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_Group_GroupId",
                table: "Transaction");

            migrationBuilder.RenameColumn(
                name: "GroupId",
                table: "Transaction",
                newName: "RuleVersionId");

            migrationBuilder.RenameIndex(
                name: "IX_Transaction_GroupId",
                table: "Transaction",
                newName: "IX_Transaction_RuleVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_RuleVersion_RuleVersionId",
                table: "Transaction",
                column: "RuleVersionId",
                principalTable: "RuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_RuleVersion_RuleVersionId",
                table: "Transaction");

            migrationBuilder.RenameColumn(
                name: "RuleVersionId",
                table: "Transaction",
                newName: "GroupId");

            migrationBuilder.RenameIndex(
                name: "IX_Transaction_RuleVersionId",
                table: "Transaction",
                newName: "IX_Transaction_GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_Group_GroupId",
                table: "Transaction",
                column: "GroupId",
                principalTable: "Group",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
