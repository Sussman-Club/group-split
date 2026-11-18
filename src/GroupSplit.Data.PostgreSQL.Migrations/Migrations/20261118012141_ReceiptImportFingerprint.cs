using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptImportFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportFingerprint",
                table: "Receipt",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipt_OwnerUserId_ImportFingerprint",
                table: "Receipt",
                columns: new[] { "OwnerUserId", "ImportFingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Receipt_OwnerUserId_ImportFingerprint",
                table: "Receipt");

            migrationBuilder.DropColumn(
                name: "ImportFingerprint",
                table: "Receipt");
        }
    }
}
