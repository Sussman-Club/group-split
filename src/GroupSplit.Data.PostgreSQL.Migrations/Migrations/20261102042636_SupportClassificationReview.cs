using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class SupportClassificationReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClassificationNeedsReview",
                table: "SupportTicket",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicket_ClassificationNeedsReview",
                table: "SupportTicket",
                column: "ClassificationNeedsReview");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupportTicket_ClassificationNeedsReview",
                table: "SupportTicket");

            migrationBuilder.DropColumn(
                name: "ClassificationNeedsReview",
                table: "SupportTicket");
        }
    }
}
