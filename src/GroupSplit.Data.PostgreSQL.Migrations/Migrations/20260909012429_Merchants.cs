using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// The merchant becomes a row of its own, and the ledger gets to point at it.
    /// </summary>
    /// <remarks>
    /// Written by hand around the scaffolded schema changes, because the order matters:
    /// <c>BankTransaction.LogoUrl</c> is the only place the logos exist, so the table is
    /// created and filled from that column before the column goes. Scaffolded on its own
    /// this migration dropped it first and every logo with it.
    /// <para>
    /// One row per distinct merchant name, folded the same way <c>MerchantDirectory</c>
    /// folds it -- trimmed and lower-cased -- so the rows this writes are exactly the rows
    /// the resolver will find afterwards instead of inserting again.
    /// </para>
    /// </remarks>
    public partial class Merchants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Merchant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LogoUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Merchant", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Merchant_NormalizedName",
                table: "Merchant",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                table: "Transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                table: "BankTransaction",
                type: "uuid",
                nullable: true);

            // One merchant per name the bank has ever used. The display name comes off the
            // earliest row, and the logo off the earliest row that had one at all -- which
            // is what the FILTER is for, since a provider that only started sending logos
            // last month should still light up a shop first seen last year.
            migrationBuilder.Sql(
                """
                INSERT INTO "Merchant" ("Id", "Name", "NormalizedName", "LogoUrl", "FirstSeenAt")
                SELECT gen_random_uuid(),
                       (array_agg(btrim("MerchantName") ORDER BY "ImportedAt"))[1],
                       lower(btrim("MerchantName")),
                       (array_agg("LogoUrl" ORDER BY "ImportedAt")
                            FILTER (WHERE "LogoUrl" IS NOT NULL))[1],
                       min("ImportedAt")
                FROM "BankTransaction"
                WHERE "MerchantName" IS NOT NULL AND btrim("MerchantName") <> ''
                GROUP BY lower(btrim("MerchantName"));
                """);

            migrationBuilder.Sql(
                """
                UPDATE "BankTransaction" AS imported
                SET "MerchantId" = merchant."Id"
                FROM "Merchant" AS merchant
                WHERE merchant."NormalizedName" = lower(btrim(imported."MerchantName"));
                """);

            // And onto the ledger, through the link filing already left behind. An expense
            // somebody typed in has no bank row and stays null, which is what null means
            // here: it went to a person, or to a shop nobody wrote down.
            migrationBuilder.Sql(
                """
                UPDATE "Transaction" AS ledger
                SET "MerchantId" = imported."MerchantId"
                FROM "BankTransaction" AS imported
                WHERE imported."Id" = ledger."BankTransactionId"
                  AND imported."MerchantId" IS NOT NULL;
                """);

            // Only now: everything it held is on the merchant.
            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "BankTransaction");

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_MerchantId",
                table: "Transaction",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransaction_MerchantId",
                table: "BankTransaction",
                column: "MerchantId");

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransaction_Merchant_MerchantId",
                table: "BankTransaction",
                column: "MerchantId",
                principalTable: "Merchant",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_Merchant_MerchantId",
                table: "Transaction",
                column: "MerchantId",
                principalTable: "Merchant",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankTransaction_Merchant_MerchantId",
                table: "BankTransaction");

            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_Merchant_MerchantId",
                table: "Transaction");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_MerchantId",
                table: "Transaction");

            migrationBuilder.DropIndex(
                name: "IX_BankTransaction_MerchantId",
                table: "BankTransaction");

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "BankTransaction",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            // Back onto the row before the table it came from goes. Rolling this back loses
            // the merchant an expense pointed at -- the ledger had no column for that before
            // this migration -- but it must not also lose the inbox's logos.
            migrationBuilder.Sql(
                """
                UPDATE "BankTransaction" AS imported
                SET "LogoUrl" = merchant."LogoUrl"
                FROM "Merchant" AS merchant
                WHERE merchant."Id" = imported."MerchantId";
                """);

            migrationBuilder.DropColumn(
                name: "MerchantId",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                table: "BankTransaction");

            migrationBuilder.DropTable(
                name: "Merchant");
        }
    }
}
