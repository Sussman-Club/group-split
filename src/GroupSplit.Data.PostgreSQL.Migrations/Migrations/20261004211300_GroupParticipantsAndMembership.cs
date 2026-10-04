using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// Everything this branch asks of the schema about a group's people, in one step: who has
    /// left and how, the history of every membership, what each user row is, one "all for
    /// one" rule per member and invitee, one join link in force, and keys that refuse to
    /// delete money with a person.
    /// </summary>
    /// <remarks>
    /// The backfills run after the columns and the history table exist and before any of the
    /// constraints that depend on them, so each constraint is created over data that already
    /// satisfies it. See <c>docs/participants.md</c>, "What the database holds it to".
    /// </remarks>
    public partial class GroupParticipantsAndMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Category_SplitRule_DefaultSplitRuleId",
                table: "Category");

            migrationBuilder.DropForeignKey(
                name: "FK_Group_SplitRule_DefaultSplitRuleId",
                table: "Group");

            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_User_UserId",
                table: "Transaction");

            migrationBuilder.DropForeignKey(
                name: "FK_TransactionSplit_User_UserId",
                table: "TransactionSplit");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "User",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Account");

            migrationBuilder.AddColumn<Guid>(
                name: "ProvisionedForUserId",
                table: "SplitRule",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeftAt",
                table: "GroupUser",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeftByUserId",
                table: "GroupUser",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeftReason",
                table: "GroupUser",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupMembershipEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembershipEvent", x => x.Id);
                    table.CheckConstraint("CK_GroupMembershipEvent_Kind", "\"Kind\" IN ('Joined', 'Rejoined', 'Left', 'Removed', 'AccountDeleted')");
                    table.ForeignKey(
                        name: "FK_GroupMembershipEvent_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMembershipEvent_User_ByUserId",
                        column: x => x.ByUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GroupMembershipEvent_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Every pending invitee gets the rule every member already has: all of it is for
            // them. Invitations sent from now on provision it as they are made; this covers
            // the ones already waiting, with the SQL AllForOneMemberSplitRules used for the
            // members. A stand-in has its invitation's name in FirstName and no address, so
            // the stem is "All for Luz". Collisions -- two invitees called Dani, or a rule the
            // group wrote with that name -- get the head of the stand-in's id. MATERIALIZED
            // because the ids are generated in it and read three times.
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

            // Whose each built-in "all for one" rule is, from its open version -- the members'
            // and the invitees' just made. Where a person somehow has two, the oldest stays
            // theirs and the rest are retired the way a hand-over retires one: kept, since
            // something may divide by them, but no longer built in.
            migrationBuilder.Sql(
                """
                WITH provisioned AS (
                    SELECT r."Id" AS rule_id,
                           s."UserId" AS user_id,
                           row_number() OVER (PARTITION BY r."GroupId", s."UserId"
                                              ORDER BY v."StartedAt", r."Id") AS rank
                    FROM "SplitRule" r
                    JOIN "SplitRuleVersion" v ON v."SplitRuleId" = r."Id" AND v."SupersededAt" IS NULL
                    JOIN "SoleSplitRuleVersion" s ON s."Id" = v."Id"
                    WHERE r."BuiltIn")
                UPDATE "SplitRule" r
                SET "ProvisionedForUserId" = CASE WHEN p.rank = 1 THEN p.user_id END,
                    "BuiltIn" = p.rank = 1
                FROM provisioned p
                WHERE p.rule_id = r."Id";
                """);

            // What each user row is, read off what it lacks -- the inference the column
            // replaces, made once. A stand-in has no identity and no address but a name, and
            // is never a member; a deleted account has no identity and nothing left at all.
            // Everything else is an account, including a member who has not signed in yet.
            migrationBuilder.Sql(
                """
                UPDATE "User" u SET "Kind" = 'StandIn'
                WHERE NOT EXISTS (SELECT 1 FROM "UserIdentity" i WHERE i."UserId" = u."Id")
                  AND u."Email" IS NULL
                  AND u."FirstName" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "GroupUser" m WHERE m."UsersId" = u."Id");

                UPDATE "User" u SET "Kind" = 'Deleted'
                WHERE NOT EXISTS (SELECT 1 FROM "UserIdentity" i WHERE i."UserId" = u."Id")
                  AND u."FirstName" IS NULL
                  AND u."LastName" IS NULL
                  AND u."Email" IS NULL;

                ALTER TABLE "User" ALTER COLUMN "Kind" DROP DEFAULT;
                """);

            // One unrevoked join link per group: the newest stays, anything older is closed --
            // at its expiry if it had lapsed, which is how the service closes those too.
            migrationBuilder.Sql(
                """
                UPDATE "GroupJoinLink" l
                SET "RevokedAt" = LEAST(l."ExpiresAt", now())
                WHERE l."RevokedAt" IS NULL
                  AND EXISTS (
                      SELECT 1 FROM "GroupJoinLink" newer
                      WHERE newer."GroupId" = l."GroupId"
                        AND newer."RevokedAt" IS NULL
                        AND (newer."CreatedAt", newer."Id") > (l."CreatedAt", l."Id"));
                """);

            // The history so far: every standing membership's join. Before this a departure
            // deleted the row, so there is nothing else to recover.
            migrationBuilder.Sql(
                """
                INSERT INTO "GroupMembershipEvent" ("Id", "GroupId", "UserId", "Kind", "At", "ByUserId")
                SELECT gen_random_uuid(), m."GroupsId", m."UsersId", 'Joined', m."JoinedAt", m."UsersId"
                FROM "GroupUser" m;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_DeletedIsAnonymous",
                table: "User",
                sql: "\"Kind\" <> 'Deleted' OR (\"FirstName\" IS NULL AND \"LastName\" IS NULL AND \"Email\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_Kind",
                table: "User",
                sql: "\"Kind\" IN ('Account', 'StandIn', 'Deleted')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_StandInHasNoEmail",
                table: "User",
                sql: "\"Kind\" <> 'StandIn' OR \"Email\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRule_GroupId_ProvisionedForUserId",
                table: "SplitRule",
                columns: new[] { "GroupId", "ProvisionedForUserId" },
                unique: true,
                filter: "\"ProvisionedForUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRule_ProvisionedForUserId",
                table: "SplitRule",
                column: "ProvisionedForUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SplitRule_ProvisionedOnlyBuiltIn",
                table: "SplitRule",
                sql: "\"ProvisionedForUserId\" IS NULL OR \"BuiltIn\"");

            migrationBuilder.CreateIndex(
                name: "IX_GroupUser_LeftByUserId",
                table: "GroupUser",
                column: "LeftByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupUser_Departure",
                table: "GroupUser",
                sql: "(\"LeftAt\" IS NULL) = (\"LeftReason\" IS NULL) AND (\"LeftAt\" IS NOT NULL OR \"LeftByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupUser_LeftReason",
                table: "GroupUser",
                sql: "\"LeftReason\" IN ('Left', 'Removed', 'AccountDeleted')");

            migrationBuilder.CreateIndex(
                name: "IX_GroupJoinLink_GroupId_Unrevoked",
                table: "GroupJoinLink",
                column: "GroupId",
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembershipEvent_ByUserId",
                table: "GroupMembershipEvent",
                column: "ByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembershipEvent_GroupId_At",
                table: "GroupMembershipEvent",
                columns: new[] { "GroupId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembershipEvent_UserId",
                table: "GroupMembershipEvent",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Category_SplitRule_DefaultSplitRuleId",
                table: "Category",
                column: "DefaultSplitRuleId",
                principalTable: "SplitRule",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Group_SplitRule_DefaultSplitRuleId",
                table: "Group",
                column: "DefaultSplitRuleId",
                principalTable: "SplitRule",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_GroupUser_User_LeftByUserId",
                table: "GroupUser",
                column: "LeftByUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_SplitRule_User_ProvisionedForUserId",
                table: "SplitRule",
                column: "ProvisionedForUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_User_UserId",
                table: "Transaction",
                column: "UserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TransactionSplit_User_UserId",
                table: "TransactionSplit",
                column: "UserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without LeftAt a closed row reads as a current membership, so dropping it as it
            // stands would put everybody who has left back into their groups. Before this a
            // departure deleted the row, which is what this goes back to.
            migrationBuilder.Sql("""DELETE FROM "GroupUser" WHERE "LeftAt" IS NOT NULL;""");

            // The invitees' rules, where nothing divides by them yet. A rule something uses
            // stays: deleting it would take the expense's division with it.
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

            migrationBuilder.DropForeignKey(
                name: "FK_Category_SplitRule_DefaultSplitRuleId",
                table: "Category");

            migrationBuilder.DropForeignKey(
                name: "FK_Group_SplitRule_DefaultSplitRuleId",
                table: "Group");

            migrationBuilder.DropForeignKey(
                name: "FK_GroupUser_User_LeftByUserId",
                table: "GroupUser");

            migrationBuilder.DropForeignKey(
                name: "FK_SplitRule_User_ProvisionedForUserId",
                table: "SplitRule");

            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_User_UserId",
                table: "Transaction");

            migrationBuilder.DropForeignKey(
                name: "FK_TransactionSplit_User_UserId",
                table: "TransactionSplit");

            migrationBuilder.DropTable(
                name: "GroupMembershipEvent");

            migrationBuilder.DropCheckConstraint(
                name: "CK_User_DeletedIsAnonymous",
                table: "User");

            migrationBuilder.DropCheckConstraint(
                name: "CK_User_Kind",
                table: "User");

            migrationBuilder.DropCheckConstraint(
                name: "CK_User_StandInHasNoEmail",
                table: "User");

            migrationBuilder.DropIndex(
                name: "IX_SplitRule_GroupId_ProvisionedForUserId",
                table: "SplitRule");

            migrationBuilder.DropIndex(
                name: "IX_SplitRule_ProvisionedForUserId",
                table: "SplitRule");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SplitRule_ProvisionedOnlyBuiltIn",
                table: "SplitRule");

            migrationBuilder.DropIndex(
                name: "IX_GroupUser_LeftByUserId",
                table: "GroupUser");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupUser_Departure",
                table: "GroupUser");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupUser_LeftReason",
                table: "GroupUser");

            migrationBuilder.DropIndex(
                name: "IX_GroupJoinLink_GroupId_Unrevoked",
                table: "GroupJoinLink");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ProvisionedForUserId",
                table: "SplitRule");

            migrationBuilder.DropColumn(
                name: "LeftAt",
                table: "GroupUser");

            migrationBuilder.DropColumn(
                name: "LeftByUserId",
                table: "GroupUser");

            migrationBuilder.DropColumn(
                name: "LeftReason",
                table: "GroupUser");

            migrationBuilder.AddForeignKey(
                name: "FK_Category_SplitRule_DefaultSplitRuleId",
                table: "Category",
                column: "DefaultSplitRuleId",
                principalTable: "SplitRule",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Group_SplitRule_DefaultSplitRuleId",
                table: "Group",
                column: "DefaultSplitRuleId",
                principalTable: "SplitRule",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_User_UserId",
                table: "Transaction",
                column: "UserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TransactionSplit_User_UserId",
                table: "TransactionSplit",
                column: "UserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
