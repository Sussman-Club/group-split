using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class SplitRuleKindBecomesASubtype : Migration
    {
        /// <inheritdoc />
        /// <summary>
        /// Replaces the <c>Kind</c> column with a discriminator: which kind of rule it is
        /// becomes which type it is, and how it divides becomes that type's handler rather
        /// than a switch on the column.
        /// </summary>
        /// <remarks>
        /// EF warns that dropping <c>Kind</c> may lose data. It cannot here: nothing has
        /// ever written a split rule. The table was added two migrations ago for a cut-over
        /// that has not reached it yet, so it is empty in every database that exists.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SplitRule");

            migrationBuilder.AddColumn<string>(
                name: "Discriminator",
                table: "SplitRule",
                type: "character varying(21)",
                maxLength: 21,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRule_Discriminator",
                table: "SplitRule",
                column: "Discriminator");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SplitRule_Discriminator",
                table: "SplitRule");

            migrationBuilder.DropColumn(
                name: "Discriminator",
                table: "SplitRule");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "SplitRule",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
