using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations;

public partial class ItemRulesOwnExpense : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve existing single-expense receipts and turn their claim weights into saved rules.
        // The removed multi-expense/bank-only workflow has no lossless one-expense equivalent.
        // Refuse before changing anything instead of dropping those bills or altering ledger amounts.
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (
                    SELECT 1 FROM "Receipt" r LEFT JOIN "ReceiptItem" i ON i."ReceiptId" = r."Id"
                    GROUP BY r."Id" HAVING COUNT(DISTINCT i."ExpenseId") <> 1
                        OR COUNT(*) FILTER (WHERE i."ExpenseId" IS NULL) > 0
                ) OR EXISTS (
                    SELECT 1 FROM "Transaction" WHERE "BankTransactionId" IS NOT NULL
                    GROUP BY "BankTransactionId" HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION 'ItemRulesOwnExpense requires one expense per bill and per bank row. Reconcile shared or unfiled bills before migrating; no data has been changed.';
                END IF;
            END $$;

            ALTER TABLE "Receipt" ADD COLUMN "ExpenseId" uuid;
            UPDATE "Receipt" r SET "ExpenseId" = (
                SELECT i."ExpenseId" FROM "ReceiptItem" i WHERE i."ReceiptId" = r."Id" LIMIT 1);
            ALTER TABLE "Receipt" ALTER COLUMN "ExpenseId" SET NOT NULL;
            ALTER TABLE "ReceiptItem" ADD COLUMN "SplitRuleVersionId" uuid;

            CREATE TEMP TABLE item_rule_conversion ON COMMIT DROP AS
                SELECT i."Id" AS item_id, gen_random_uuid() AS rule_id, gen_random_uuid() AS version_id,
                    t."GroupId" AS group_id, i."Name" AS item_name
                FROM "ReceiptItem" i JOIN "Receipt" r ON r."Id" = i."ReceiptId"
                JOIN "Transaction" t ON t."Id" = r."ExpenseId"
                WHERE EXISTS (SELECT 1 FROM "ReceiptItemClaim" c WHERE c."ReceiptItemId" = i."Id");

            INSERT INTO "SplitRule" ("Id", "GroupId", "Name")
                SELECT rule_id, group_id, 'Item ' || item_id::text FROM item_rule_conversion;
            INSERT INTO "SplitRuleVersion" ("Id", "SplitRuleId", "StartedAt", "SupersededAt", "Discriminator")
                SELECT version_id, rule_id, CURRENT_TIMESTAMP, NULL, 'SharesSplitRuleVersion' FROM item_rule_conversion;
            INSERT INTO "SplitRuleParticipant" ("Id", "SplitRuleVersionId", "UserId", "Weight")
                SELECT gen_random_uuid(), m.version_id, c."UserId", c."Weight"
                FROM "ReceiptItemClaim" c JOIN item_rule_conversion m ON m.item_id = c."ReceiptItemId";
            UPDATE "ReceiptItem" i SET "SplitRuleVersionId" = m.version_id
                FROM item_rule_conversion m WHERE m.item_id = i."Id";

            DROP TABLE "ReceiptItemClaim";
            ALTER TABLE "ReceiptItem" DROP COLUMN "ExpenseId";
            ALTER TABLE "Receipt" DROP COLUMN "BankTransactionId";
            DROP INDEX "IX_Transaction_BankTransactionId";
            CREATE UNIQUE INDEX "IX_Transaction_BankTransactionId" ON "Transaction" ("BankTransactionId");
            CREATE UNIQUE INDEX "IX_Receipt_ExpenseId" ON "Receipt" ("ExpenseId");
            CREATE INDEX "IX_ReceiptItem_SplitRuleVersionId" ON "ReceiptItem" ("SplitRuleVersionId");
            ALTER TABLE "Receipt" ADD CONSTRAINT "FK_Receipt_Transaction_ExpenseId"
                FOREIGN KEY ("ExpenseId") REFERENCES "Transaction" ("Id") ON DELETE CASCADE;
            ALTER TABLE "ReceiptItem" ADD CONSTRAINT "FK_ReceiptItem_SplitRuleVersion_SplitRuleVersionId"
                FOREIGN KEY ("SplitRuleVersionId") REFERENCES "SplitRuleVersion" ("Id") ON DELETE RESTRICT;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Item rules cannot be losslessly converted back to claims. Restore a pre-migration backup to roll back.");
}
