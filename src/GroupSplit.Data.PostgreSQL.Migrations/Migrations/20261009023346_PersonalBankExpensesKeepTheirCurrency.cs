using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PersonalBankExpensesKeepTheirCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only. Keeping a bank row personal used to stamp the expense with the
            // default currency whatever the bank said (#320), so a 20.00 EUR charge became a
            // 20.00 USD expense. Nothing could change an expense's currency afterwards, so a
            // personal expense filed from a row that disagrees with it is one of those, and
            // the row is the record of what it was really in. Group expenses are left alone:
            // filing into a group refused a row in another currency, so theirs already agree.
            migrationBuilder.Sql(
                """
                UPDATE "Transaction" AS t
                SET "Currency" = b."Currency"
                FROM "BankTransaction" AS b
                WHERE t."BankTransactionId" = b."Id"
                  AND t."GroupId" IS NULL
                  AND t."Currency" <> b."Currency";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the currency this put right was never the one the money was
            // spent in, and writing it back would only reintroduce the bug.
        }
    }
}
