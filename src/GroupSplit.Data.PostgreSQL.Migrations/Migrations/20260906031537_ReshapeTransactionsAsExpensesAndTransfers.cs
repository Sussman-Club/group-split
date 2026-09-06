using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ReshapeTransactionsAsExpensesAndTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "RuleVersionId",
                table: "Transaction",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Transaction",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<string>(
                name: "Discriminator",
                table: "Transaction",
                type: "character varying(13)",
                maxLength: 13,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_Discriminator",
                table: "Transaction",
                column: "Discriminator");

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_GroupId_DateTime",
                table: "Transaction",
                columns: new[] { "GroupId", "DateTime" });

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_Group_GroupId",
                table: "Transaction",
                column: "GroupId",
                principalTable: "Group",
                principalColumn: "Id");

            Backfill(migrationBuilder);
        }

        /// <summary>
        /// Moves the rows the columns above were added for: every transaction becomes an
        /// expense with its splits written out, and every settlement pair collapses into a
        /// single transfer.
        /// </summary>
        /// <remarks>
        /// One-way, and destructive where it collapses the settlement pairs. That is safe
        /// here only because the only data is seed and development data; a database with
        /// real history in it would need a plan this one does not attempt.
        /// <para>
        /// The arithmetic below is spelled out rather than calling into
        /// <c>SplitCalculator</c>, on purpose. A migration has to keep computing what it
        /// computed the day it ran, and must not start following later edits to production
        /// code.
        /// </para>
        /// </remarks>
        private static void Backfill(MigrationBuilder migrationBuilder)
        {
            // The group used to be reachable only by walking the rule a transaction was
            // divided by. A transfer is divided by no rule, so it has to be a column.
            migrationBuilder.Sql("""
                UPDATE "Transaction" AS t
                SET "GroupId" = r."GroupId"
                FROM "RuleVersion" AS rv
                JOIN "Rule" AS r ON r."Id" = rv."RuleId"
                WHERE rv."Id" = t."RuleVersionId";
                """);

            // Everything is an expense until the settlement pairs below say otherwise.
            migrationBuilder.Sql("""UPDATE "Transaction" SET "Discriminator" = 'Expense';""");

            // A percentage rule, divided the way the balance query used to divide it on
            // every read: each participant truncated to the cent, and the remainder left
            // with one of them so that the parts sum to the whole.
            //
            // Who holds the remainder is the payer when the payer is a participant, which
            // is what the old query did. When they are not -- somebody paying for a split
            // they are no part of -- the old query left those truncated cents belonging to
            // nobody, so the group's balances did not sum to zero. Here they go to the
            // largest share, ties broken by the smaller id, which is what the application
            // now does. It is the one place this migration knowingly corrects rather than
            // preserves, and it moves at most a cent or two.
            migrationBuilder.Sql("""
                WITH participants AS (
                    SELECT t."Id" AS transaction_id,
                           t."Amount" AS amount,
                           t."UserId" AS payer_id,
                           pru."UserId" AS user_id,
                           CAST(pru."Percentage" AS numeric) AS percentage
                    FROM "Transaction" AS t
                    JOIN "PercentRuleVersion" AS prv ON prv."Id" = t."RuleVersionId"
                    JOIN "PercentRuleUser" AS pru ON pru."RuleVersionId" = prv."Id"
                ),
                holder AS (
                    SELECT DISTINCT ON (transaction_id) transaction_id, user_id
                    FROM participants
                    ORDER BY transaction_id,
                             (user_id = payer_id AND percentage > 0) DESC,
                             percentage DESC,
                             user_id
                ),
                shares AS (
                    SELECT p.transaction_id,
                           p.user_id,
                           p.amount,
                           CASE WHEN h.user_id = p.user_id
                                THEN NULL
                                ELSE trunc(p.amount * p.percentage) / 100
                           END AS share
                    FROM participants AS p
                    JOIN holder AS h ON h.transaction_id = p.transaction_id
                )
                INSERT INTO "TransactionSplit" ("Id", "TransactionId", "UserId", "Amount")
                SELECT gen_random_uuid(),
                       s.transaction_id,
                       s.user_id,
                       COALESCE(
                           s.share,
                           s.amount - COALESCE((SELECT SUM(other.share)
                                                FROM shares AS other
                                                WHERE other.transaction_id = s.transaction_id
                                                  AND other.share IS NOT NULL), 0))
                FROM shares AS s;
                """);

            // A personal expense divides with nobody, so all of it is the payer's.
            migrationBuilder.Sql("""
                INSERT INTO "TransactionSplit" ("Id", "TransactionId", "UserId", "Amount")
                SELECT gen_random_uuid(), t."Id", t."UserId", t."Amount"
                FROM "Transaction" AS t
                JOIN "PersonalRuleVersion" AS prv ON prv."Id" = t."RuleVersionId";
                """);

            CollapseSettlementPairs(migrationBuilder);
        }

        /// <summary>
        /// Turns each matched pair of settlement rows into the one transfer that says the
        /// same thing.
        /// </summary>
        /// <remarks>
        /// Settling wrote two rows sharing a group and an instant: <c>+amount</c> paid by
        /// one member and <c>-amount</c> paid by the other. Balance was paid minus owed and
        /// a settlement owed nothing, so the positive row's payer rose by the amount and the
        /// negative row's payer fell by it.
        /// <para>
        /// A transfer reproduces exactly that -- its payer rises by the amount, and its one
        /// split makes the recipient fall by it -- so the money moves <em>from</em> the
        /// positive row's payer <em>to</em> the negative row's. The positive row is kept and
        /// becomes the transfer; the negative row is deleted, having only ever existed to
        /// carry the other half of a sign.
        /// </para>
        /// <para>
        /// This preserves balances. Whether that direction is what somebody pressing Settle
        /// meant is a different question, and not one for a migration whose whole job is to
        /// move nothing.
        /// </para>
        /// </remarks>
        private static void CollapseSettlementPairs(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    unpaired integer;
                BEGIN
                    CREATE TEMP TABLE settlement_row ON COMMIT DROP AS
                    SELECT t."Id" AS id,
                           t."Amount" AS amount,
                           t."DateTime" AS moment,
                           t."UserId" AS user_id,
                           t."GroupId" AS group_id
                    FROM "Transaction" AS t
                    JOIN "SettlementRuleVersion" AS srv ON srv."Id" = t."RuleVersionId";

                    -- Each negative row is claimed by at most one positive row, so two
                    -- settlements recorded in the same instant cannot borrow each other's
                    -- halves.
                    CREATE TEMP TABLE transfer_pair ON COMMIT DROP AS
                    SELECT DISTINCT ON (paired.negative_id)
                           paired.positive_id, paired.negative_id, paired.from_id,
                           paired.to_id, paired.amount
                    FROM (
                        SELECT DISTINCT ON (positive.id)
                               positive.id AS positive_id,
                               negative.id AS negative_id,
                               positive.user_id AS from_id,
                               negative.user_id AS to_id,
                               positive.amount AS amount
                        FROM settlement_row AS positive
                        JOIN settlement_row AS negative
                          ON negative.group_id IS NOT DISTINCT FROM positive.group_id
                         AND negative.moment = positive.moment
                         AND negative.amount = -positive.amount
                        WHERE positive.amount > 0
                        ORDER BY positive.id, negative.id
                    ) AS paired
                    ORDER BY paired.negative_id, paired.positive_id;

                    INSERT INTO "TransactionSplit" ("Id", "TransactionId", "UserId", "Amount")
                    SELECT gen_random_uuid(), positive_id, to_id, amount FROM transfer_pair;

                    UPDATE "Transaction"
                    SET "Discriminator" = 'Transfer',
                        "RuleVersionId" = NULL,
                        "Name" = 'Settlement'
                    WHERE "Id" IN (SELECT positive_id FROM transfer_pair);

                    DELETE FROM "Transaction"
                    WHERE "Id" IN (SELECT negative_id FROM transfer_pair);

                    -- Anything still hanging off a settlement rule is half a settlement, and
                    -- guessing what it meant would move somebody's money in silence.
                    SELECT count(*) INTO unpaired
                    FROM "Transaction" AS t
                    JOIN "SettlementRuleVersion" AS srv ON srv."Id" = t."RuleVersionId";

                    IF unpaired > 0 THEN
                        RAISE EXCEPTION
                            'ReshapeTransactionsAsExpensesAndTransfers: % settlement row(s) could not be paired into a transfer. Resolve them by hand before migrating.',
                            unpaired;
                    END IF;
                END $$;
                """);

            // The settlement rule existed to be excluded from things. Nothing points at it
            // now, and leaving it behind would put "Settlement" in a group's category list
            // with nothing able to record against it.
            migrationBuilder.Sql("""
                DELETE FROM "SettlementRuleVersion"
                WHERE "Id" IN (SELECT rv."Id" FROM "RuleVersion" AS rv
                               JOIN "Rule" AS r ON r."Id" = rv."RuleId"
                               WHERE r."Category" = 'Settlement');
                """);

            migrationBuilder.Sql("""
                DELETE FROM "RuleVersion"
                WHERE "RuleId" IN (SELECT "Id" FROM "Rule" WHERE "Category" = 'Settlement');
                """);

            migrationBuilder.Sql("""DELETE FROM "Rule" WHERE "Category" = 'Settlement';""");
        }

        /// <inheritdoc />
        /// <summary>
        /// Undoes the columns, and refuses when the rows cannot follow them back.
        /// </summary>
        /// <remarks>
        /// A transfer has no shape in the old model. It was two rows there, and this
        /// migration deleted the second one, so going back would have to invent it -- and
        /// the old <c>RuleVersionId</c> is not nullable, so without this the attempt fails
        /// later and less clearly, having already dropped a column or two.
        /// <para>
        /// A database where nothing was ever settled has no transfers and goes back
        /// cleanly, which is the case worth keeping reversible.
        /// </para>
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    transfers integer;
                BEGIN
                    SELECT count(*) INTO transfers FROM "Transaction" WHERE "Discriminator" = 'Transfer';

                    IF transfers > 0 THEN
                        RAISE EXCEPTION
                            'ReshapeTransactionsAsExpensesAndTransfers cannot be reverted: % transfer(s) exist, and the pair of rows each one replaced was deleted going forward.',
                            transfers;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""DELETE FROM "TransactionSplit";""");

            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_Group_GroupId",
                table: "Transaction");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_Discriminator",
                table: "Transaction");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_GroupId_DateTime",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "Discriminator",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Transaction");

            migrationBuilder.AlterColumn<Guid>(
                name: "RuleVersionId",
                table: "Transaction",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
