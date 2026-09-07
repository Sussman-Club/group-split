using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Keeps more of what the bank actually sends about a row.
    /// </summary>
    /// <remarks>
    /// The import kept ten of Plaid's thirty-odd fields and left the rest in the raw
    /// payload, which nothing read. These five are the ones that change what somebody sees:
    /// the merchant's logo, the finer category, where and how it was paid, and -- the one
    /// that matters most -- the date the money was actually spent.
    /// <para>
    /// A card charge posts days after the event, so the date already stored is the posting
    /// date and is not the one anybody remembers. <c>AuthorizedDate</c> is, where the
    /// provider knows it.
    /// </para>
    /// <para>
    /// All nullable and all additive: a provider that says none of this is an ordinary
    /// case, and rows imported before this stay exactly as they were.
    /// </para>
    /// </remarks>
    public partial class BankRowDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AuthorizedDate",
                table: "BankTransaction",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "BankTransaction",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "BankTransaction",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentChannel",
                table: "BankTransaction",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderCategoryDetailed",
                table: "BankTransaction",
                type: "character varying(96)",
                maxLength: 96,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorizedDate",
                table: "BankTransaction");

            migrationBuilder.DropColumn(
                name: "City",
                table: "BankTransaction");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "BankTransaction");

            migrationBuilder.DropColumn(
                name: "PaymentChannel",
                table: "BankTransaction");

            migrationBuilder.DropColumn(
                name: "ProviderCategoryDetailed",
                table: "BankTransaction");
        }
    }
}
