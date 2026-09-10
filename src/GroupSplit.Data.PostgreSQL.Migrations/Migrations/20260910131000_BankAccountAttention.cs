using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class BankAccountAttention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AccessRevoked",
                table: "LinkedAccount",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AccountsNotShared",
                table: "BankConnection",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SignInExpiring",
                table: "BankConnection",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessRevoked",
                table: "LinkedAccount");

            migrationBuilder.DropColumn(
                name: "AccountsNotShared",
                table: "BankConnection");

            migrationBuilder.DropColumn(
                name: "SignInExpiring",
                table: "BankConnection");
        }
    }
}
