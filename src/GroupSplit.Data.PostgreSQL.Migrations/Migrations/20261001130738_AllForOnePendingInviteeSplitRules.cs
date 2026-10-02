using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// Gives every pending invitee the rule every member already has: all of it is for them.
    /// </summary>
    /// <remarks>
    /// Invitations sent from now on provision it as they are made. This covers the ones
    /// already waiting, the same way <c>AllForOneMemberSplitRules</c> covered the members who
    /// were already in their groups -- and with the same SQL, sourced from the invitations'
    /// stand-ins rather than from memberships. No model change: the rule, its version and the
    /// flag all exist.
    /// </remarks>
    public partial class AllForOnePendingInviteeSplitRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A stand-in has its invitation's name in FirstName and no address, so the stem is
            // "All for Luz". Collisions -- two invitees called Dani, or a rule the group wrote
            // with that name -- get the head of the stand-in's id, as the member backfill did.
            //
            // MATERIALIZED because the ids are generated in it and read three times; see the
            // member backfill for what happens without it.
            migrationBuilder.Sql(
                """
                WITH candidate AS (
                    SELECT i."GroupId" AS group_id,
                           i."ParticipantUserId" AS user_id,
                           left('All for ' || COALESCE(
                                NULLIF(btrim(COALESCE(u."FirstName", '') || ' ' || COALESCE(u."LastName", '')), ''),
                                u."Email",
                                i."Name",
                                'one member'), 49) AS stem
                    FROM "GroupInvitation" i
                    JOIN "User" u ON u."Id" = i."ParticipantUserId"
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM "SoleSplitRuleVersion" s
                        JOIN "SplitRuleVersion" v ON v."Id" = s."Id"
                        JOIN "SplitRule" r ON r."Id" = v."SplitRuleId"
                        WHERE r."GroupId" = i."GroupId"
                          AND r."BuiltIn"
                          AND v."SupersededAt" IS NULL
                          AND s."UserId" = i."ParticipantUserId")
                ),
                named AS MATERIALIZED (
                    SELECT c.group_id,
                           c.user_id,
                           gen_random_uuid() AS rule_id,
                           gen_random_uuid() AS version_id,
                           CASE
                               WHEN EXISTS (SELECT 1 FROM "SplitRule" r
                                            WHERE r."GroupId" = c.group_id
                                              AND lower(r."Name") = lower(c.stem))
                                 OR EXISTS (SELECT 1 FROM candidate o
                                            WHERE o.group_id = c.group_id
                                              AND lower(o.stem) = lower(c.stem)
                                              AND o.user_id <> c.user_id)
                               THEN c.stem || ' (' || left(c.user_id::text, 8) || ')'
                               ELSE c.stem
                           END AS name
                    FROM candidate c
                ),
                created_rule AS (
                    INSERT INTO "SplitRule" ("Id", "GroupId", "Name", "BuiltIn")
                    SELECT rule_id, group_id, name, true FROM named
                    RETURNING "Id"
                ),
                created_version AS (
                    INSERT INTO "SplitRuleVersion" ("Id", "SplitRuleId", "StartedAt", "SupersededAt")
                    SELECT version_id, rule_id, now(), NULL FROM named
                    RETURNING "Id"
                )
                INSERT INTO "SoleSplitRuleVersion" ("Id", "UserId")
                SELECT version_id, user_id FROM named;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only what nothing has come to need: built-in rules for a still-pending invitee
            // that no expense, bill line, category or group points at. A rule an expense was
            // divided by since is that expense's history, and a rollback cannot take it
            // without rewriting what somebody owed.
            migrationBuilder.Sql(
                """
                DELETE FROM "SplitRule" r
                WHERE r."BuiltIn"
                  AND EXISTS (
                      SELECT 1
                      FROM "SplitRuleVersion" v
                      JOIN "SoleSplitRuleVersion" s ON s."Id" = v."Id"
                      JOIN "GroupInvitation" i ON i."ParticipantUserId" = s."UserId"
                                              AND i."GroupId" = r."GroupId"
                      WHERE v."SplitRuleId" = r."Id")
                  AND NOT EXISTS (
                      SELECT 1 FROM "Transaction" t
                      JOIN "SplitRuleVersion" v ON v."Id" = t."SplitRuleVersionId"
                      WHERE v."SplitRuleId" = r."Id")
                  AND NOT EXISTS (
                      SELECT 1 FROM "ReceiptItem" ri
                      JOIN "SplitRuleVersion" v ON v."Id" = ri."SplitRuleVersionId"
                      WHERE v."SplitRuleId" = r."Id")
                  AND NOT EXISTS (SELECT 1 FROM "Category" c WHERE c."DefaultSplitRuleId" = r."Id")
                  AND NOT EXISTS (SELECT 1 FROM "Group" g WHERE g."DefaultSplitRuleId" = r."Id");
                """);
        }
    }
}
