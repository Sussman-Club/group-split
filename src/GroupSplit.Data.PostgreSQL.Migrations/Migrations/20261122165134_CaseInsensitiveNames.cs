using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class CaseInsensitiveNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SplitRule_GroupId_Name",
                table: "SplitRule");

            migrationBuilder.DropIndex(
                name: "IX_Category_GroupId_Name",
                table: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRule_GroupId",
                table: "SplitRule",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Category_GroupId",
                table: "Category",
                column: "GroupId");

            // The same names, now ignoring case, so "Food" and "food" created in the same
            // instant cannot both pass the duplicate check. EF cannot express an index on an
            // expression, so these are not in the model; the services catch them by name.
            // Fails, rather than renaming anything, if a group already holds two such names.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_SplitRule_GroupId_Name" ON "SplitRule" ("GroupId", lower("Name"));
                CREATE UNIQUE INDEX "IX_Category_GroupId_Name" ON "Category" ("GroupId", lower("Name"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX "IX_SplitRule_GroupId_Name";
                DROP INDEX "IX_Category_GroupId_Name";
                """);

            migrationBuilder.DropIndex(
                name: "IX_SplitRule_GroupId",
                table: "SplitRule");

            migrationBuilder.DropIndex(
                name: "IX_Category_GroupId",
                table: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRule_GroupId_Name",
                table: "SplitRule",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Category_GroupId_Name",
                table: "Category",
                columns: new[] { "GroupId", "Name" },
                unique: true);
        }
    }
}
