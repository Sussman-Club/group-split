using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// Moves a bill's tax from one figure and a per-line flag onto the lines themselves.
    /// </summary>
    /// <remarks>
    /// The flag said whether the bill's tax was charged on a line, and the tax was then
    /// weighed over the flagged lines in proportion to their prices. That is exact only where
    /// every taxed line carries one rate; a receipt mixing 6% food with 23% household goods
    /// divided wrongly by several euros, with the total still adding up.
    /// <para>
    /// Backfilled rather than defaulted, so that every bill already stored divides exactly as
    /// it did before: the old apportioning is run once, in SQL, and its answer becomes the
    /// stored amount. On a single-rate bill -- which is every bill the flag could describe
    /// correctly -- that answer is the true per-line tax, so nothing moves.
    /// </para>
    /// <para>
    /// The column is added and filled before the flag is dropped, which the scaffolded version
    /// had the other way round: the information the backfill reads lives in the flag.
    /// </para>
    /// </remarks>
    public partial class PerLineTax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "ReceiptItem",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // The old apportioning, once. Truncate each line's share to the cent and hand the
            // leftover to the largest line, which is what Spread did -- so the lines come to
            // the receipt's tax exactly, which is the invariant the code now checks on every
            // save and every division.
            //
            // "taxed" is the flagged lines, or every line where a bill charges tax and flags
            // none: that was the old fallback, and a bill in that state has to keep dividing
            // rather than start refusing on a migration.
            migrationBuilder.Sql(
                """
                WITH charged AS (
                    SELECT i."Id",
                           i."ReceiptId",
                           i."TotalPrice",
                           r."Tax"
                    FROM "ReceiptItem" i
                    JOIN "Receipt" r ON r."Id" = i."ReceiptId"
                    WHERE r."Tax" <> 0
                      AND (i."IsTaxable"
                           OR NOT EXISTS (SELECT 1
                                          FROM "ReceiptItem" t
                                          WHERE t."ReceiptId" = i."ReceiptId"
                                            AND t."IsTaxable"))
                ),
                cut AS (
                    SELECT "Id",
                           "ReceiptId",
                           "Tax",
                           CASE WHEN SUM("TotalPrice") OVER (PARTITION BY "ReceiptId") = 0
                                THEN 0
                                ELSE TRUNC("Tax" * "TotalPrice"
                                           / SUM("TotalPrice") OVER (PARTITION BY "ReceiptId"), 2)
                           END AS share,
                           ROW_NUMBER() OVER (PARTITION BY "ReceiptId"
                                              ORDER BY "TotalPrice" DESC, "Id") AS rank
                    FROM charged
                ),
                placed AS (
                    SELECT "ReceiptId", SUM(share) AS total FROM cut GROUP BY "ReceiptId"
                )
                UPDATE "ReceiptItem" i
                SET "TaxAmount" = cut.share
                                + CASE WHEN cut.rank = 1 THEN cut."Tax" - placed.total ELSE 0 END
                FROM cut
                JOIN placed ON placed."ReceiptId" = cut."ReceiptId"
                WHERE i."Id" = cut."Id";
                """);

            migrationBuilder.DropColumn(
                name: "IsTaxable",
                table: "ReceiptItem");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTaxable",
                table: "ReceiptItem",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // A line that carried no tax on a bill that charged some was an exempt line. On a
            // bill charging none the flag said nothing either way, and true is what it
            // defaulted to.
            migrationBuilder.Sql(
                """
                UPDATE "ReceiptItem" i
                SET "IsTaxable" = false
                FROM "Receipt" r
                WHERE r."Id" = i."ReceiptId"
                  AND r."Tax" <> 0
                  AND i."TaxAmount" = 0;
                """);

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "ReceiptItem");
        }
    }
}
