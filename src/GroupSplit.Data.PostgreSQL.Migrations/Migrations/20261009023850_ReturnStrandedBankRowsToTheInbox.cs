using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// Frees the bank rows that deleting their expense left filed with nothing filed from
    /// them.
    /// </summary>
    /// <remarks>
    /// Until 2026-10-09 deleting an expense left its row <c>Filed</c>: the foreign key set
    /// itself to null and nothing moved the status. Filing, linking and ignoring all then
    /// refused the row, so it could never leave the Filed tab. Deleting an expense now puts
    /// its row back in the inbox, and this does the same for the rows stranded before.
    /// <para>
    /// A stranded row the bank has since withdrawn goes instead, as a sync would have
    /// deleted it had nobody filed it. Any receipt file still naming it is left owned by
    /// nothing, which the foreign key already does for every such file; deleting their
    /// stored objects is the cleanup described in the pull request, because a migration
    /// cannot reach the bucket.
    /// </para>
    /// <para>
    /// Data only, and with no way back: which rows were stranded is not recorded anywhere,
    /// and putting a waiting row back to Filed would strand it again.
    /// </para>
    /// </remarks>
    public partial class ReturnStrandedBankRowsToTheInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "BankTransaction" b
                WHERE b."Status" = 'Filed'
                  AND b."RemovedAt" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "Transaction" t WHERE t."BankTransactionId" = b."Id");
                """);

            migrationBuilder.Sql(
                """
                UPDATE "BankTransaction" b
                SET "Status" = 'New'
                WHERE b."Status" = 'Filed'
                  AND NOT EXISTS (SELECT 1 FROM "Transaction" t WHERE t."BankTransactionId" = b."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
