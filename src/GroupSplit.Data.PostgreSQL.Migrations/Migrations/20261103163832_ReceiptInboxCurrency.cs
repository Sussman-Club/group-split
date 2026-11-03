using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptInboxCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Receipt",
                type: "text",
                nullable: false,
                defaultValue: "USD");
            migrationBuilder.Sql("UPDATE \"Receipt\" r SET \"Currency\" = e.\"Currency\" FROM \"Transaction\" e WHERE e.\"Id\" = r.\"ExpenseId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Receipt");
        }
    }
}
