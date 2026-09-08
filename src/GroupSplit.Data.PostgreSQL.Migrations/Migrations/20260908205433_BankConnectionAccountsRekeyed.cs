using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class BankConnectionAccountsRekeyed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AccountsRekeyed",
                table: "BankConnection",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountsRekeyed",
                table: "BankConnection");
        }
    }
}
