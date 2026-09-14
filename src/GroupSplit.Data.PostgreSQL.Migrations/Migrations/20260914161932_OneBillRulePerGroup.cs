using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// One "divide it by the bill" rule per group, given rather than written.
    /// </summary>
    /// <remarks>
    /// The rule holds no settings -- what it divides by lives on the receipt, one line at a
    /// time -- so two of them are the same rule under two names, and
    /// <c>ItemizedSplitRuleHandler.SameAs</c> has always said so. Groups accumulated several
    /// anyway, one per category that wanted a name: the seeded Home group held "Dinners out"
    /// and "Takeaway", three expenses on the first and thirty-five on the second, dividing
    /// identically. Every client then had to find "the" bill rule among indistinguishable
    /// ones, and reading the first meant an expense on the second matched nothing.
    /// <para>
    /// So each group's itemized rules collapse into one. The survivor is the oldest, which
    /// keeps whichever rule the group has had longest; it becomes <c>BuiltIn</c> and is
    /// renamed, and the rest are repointed onto its open version and deleted.
    /// </para>
    /// <para>
    /// No share moves. Repointing an expense from one itemized version to another cannot
    /// change what it is divided into, because neither version says anything -- the division
    /// is read off the bill both before and after. That is what makes this collapse safe in a
    /// way that collapsing any other kind of rule would not be.
    /// </para>
    /// <para>
    /// Groups with no itemized rule at all are given one here, so that every group has one
    /// from this point on and no client has to handle its absence.
    /// </para>
    /// </remarks>
    public partial class OneBillRulePerGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Refuses rather than guesses, in the one shape this cannot repair: a line of a
            // bill is not allowed to divide by "divide it by the bill" -- that is the expense
            // pointing at itself -- and if one somehow does, repointing it would move real
            // money. Nothing in the app can write that, so this is a tripwire and not an
            // expected path.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "ReceiptItem" i
                        JOIN "ItemizedSplitRuleVersion" iv ON iv."Id" = i."SplitRuleVersionId")
                    THEN
                        RAISE EXCEPTION
                            'A receipt line divides by an itemized rule; collapsing them would move money.';
                    END IF;
                END $$;
                """);

            // The survivor per group: the oldest open itemized rule. Ordered by when its
            // version started and then by id, so the choice is the same on every database
            // this runs against rather than whatever order the rows come back in.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE bill_rule_survivor AS
                SELECT DISTINCT ON (r."GroupId")
                       r."GroupId" AS group_id,
                       r."Id"      AS rule_id,
                       v."Id"      AS version_id
                FROM "SplitRule" r
                JOIN "SplitRuleVersion" v ON v."SplitRuleId" = r."Id"
                JOIN "ItemizedSplitRuleVersion" iv ON iv."Id" = v."Id"
                WHERE v."SupersededAt" IS NULL
                ORDER BY r."GroupId", v."StartedAt", r."Id";
                """);

            // Everything else that divides by a bill, and the survivor it folds into.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE bill_rule_absorbed AS
                SELECT r."Id"  AS rule_id,
                       v."Id"  AS version_id,
                       s.rule_id    AS survivor_rule_id,
                       s.version_id AS survivor_version_id
                FROM "SplitRule" r
                JOIN "SplitRuleVersion" v ON v."SplitRuleId" = r."Id"
                JOIN "ItemizedSplitRuleVersion" iv ON iv."Id" = v."Id"
                JOIN bill_rule_survivor s ON s.group_id = r."GroupId"
                WHERE r."Id" <> s.rule_id;
                """);

            // The expenses first: every version about to go has to stop being pointed at
            // before it can be removed. Onto the survivor's open version, which divides these
            // expenses exactly as the version they held did.
            migrationBuilder.Sql(
                """
                UPDATE "Transaction" t
                SET "SplitRuleVersionId" = a.survivor_version_id
                FROM bill_rule_absorbed a
                WHERE t."SplitRuleVersionId" = a.version_id;
                """);

            // Then the categories, which point at the rule rather than at a version.
            migrationBuilder.Sql(
                """
                UPDATE "Category" c
                SET "DefaultSplitRuleId" = a.survivor_rule_id
                FROM bill_rule_absorbed a
                WHERE c."DefaultSplitRuleId" = a.rule_id;
                """);

            // And now the rules themselves, innermost table first.
            migrationBuilder.Sql(
                """
                DELETE FROM "ItemizedSplitRuleVersion" iv
                USING "SplitRuleVersion" v
                WHERE v."Id" = iv."Id"
                  AND v."SplitRuleId" IN (SELECT rule_id FROM bill_rule_absorbed);

                DELETE FROM "SplitRuleVersion" v
                WHERE v."SplitRuleId" IN (SELECT rule_id FROM bill_rule_absorbed);

                DELETE FROM "SplitRule" r
                WHERE r."Id" IN (SELECT rule_id FROM bill_rule_absorbed);
                """);

            // The survivor becomes the rule the group was given: not renamed, restated or
            // deleted from here on. Named for what it does, and suffixed where the group has
            // already used that name for something of its own.
            migrationBuilder.Sql(
                """
                UPDATE "SplitRule" r
                SET "BuiltIn" = true,
                    "Name" = CASE
                        WHEN EXISTS (SELECT 1 FROM "SplitRule" o
                                     WHERE o."GroupId" = r."GroupId"
                                       AND o."Id" <> r."Id"
                                       AND lower(o."Name") = lower('Divide by the bill'))
                        THEN 'Divide by the bill (' || left(r."Id"::text, 8) || ')'
                        ELSE 'Divide by the bill'
                    END
                FROM bill_rule_survivor s
                WHERE r."Id" = s.rule_id;
                """);

            // And every group that had none is given one, so that from here a client can
            // count on it being there.
            migrationBuilder.Sql(
                """
                WITH wanting AS (
                    SELECT g."Id" AS group_id,
                           gen_random_uuid() AS rule_id,
                           gen_random_uuid() AS version_id,
                           CASE
                               WHEN EXISTS (SELECT 1 FROM "SplitRule" o
                                            WHERE o."GroupId" = g."Id"
                                              AND lower(o."Name") = lower('Divide by the bill'))
                               THEN 'Divide by the bill (' || left(g."Id"::text, 8) || ')'
                               ELSE 'Divide by the bill'
                           END AS name
                    FROM "Group" g
                    WHERE NOT EXISTS (SELECT 1 FROM bill_rule_survivor s WHERE s.group_id = g."Id")
                ),
                created_rule AS (
                    INSERT INTO "SplitRule" ("Id", "GroupId", "Name", "BuiltIn")
                    SELECT rule_id, group_id, name, true FROM wanting
                    RETURNING "Id"
                ),
                created_version AS (
                    INSERT INTO "SplitRuleVersion" ("Id", "SplitRuleId", "StartedAt", "SupersededAt")
                    SELECT version_id, rule_id, now(), NULL FROM wanting
                    RETURNING "Id"
                )
                INSERT INTO "ItemizedSplitRuleVersion" ("Id")
                SELECT version_id FROM wanting;
                """);

            migrationBuilder.Sql(
                """
                DROP TABLE bill_rule_absorbed;
                DROP TABLE bill_rule_survivor;
                """);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The rules that were folded in are gone, and which expense named which of them is
        /// gone with them -- so down cannot put them back, and says so rather than pretending.
        /// What it can undo is the part that is a fact about the surviving rule rather than a
        /// loss: it stops being provisioned, so a group can rename or delete it again.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """
                UPDATE "SplitRule" r
                SET "BuiltIn" = false
                WHERE r."BuiltIn"
                  AND EXISTS (
                      SELECT 1
                      FROM "SplitRuleVersion" v
                      JOIN "ItemizedSplitRuleVersion" iv ON iv."Id" = v."Id"
                      WHERE v."SplitRuleId" = r."Id"
                        AND v."SupersededAt" IS NULL);
                """);
        }
    }
}
