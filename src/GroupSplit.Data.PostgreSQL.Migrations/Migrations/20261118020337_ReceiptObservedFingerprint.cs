using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptObservedFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ObservedFingerprint",
                table: "Receipt",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipt_OwnerUserId_ObservedFingerprint",
                table: "Receipt",
                columns: new[] { "OwnerUserId", "ObservedFingerprint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Receipt_OwnerUserId_ObservedFingerprint",
                table: "Receipt");

            migrationBuilder.DropColumn(
                name: "ObservedFingerprint",
                table: "Receipt");
        }
    }
}
