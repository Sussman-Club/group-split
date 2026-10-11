using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// The span a group's Activity tab opens on (#405).
    /// </summary>
    /// <remarks>
    /// Not nullable. Every group present at this moment is backfilled to This month, the span
    /// the tab opened on for all of them until now, so nothing changes for anybody until a
    /// member picks something else. Personal groups included.
    /// </remarks>
    public partial class GroupDefaultActivityPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultPeriod",
                table: "Group",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "ThisMonth");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultPeriod",
                table: "Group");
        }
    }
}
