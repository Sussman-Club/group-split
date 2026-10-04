using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class GroupManagementIntegrity : Migration
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

            // What each existing row is, read off what it lacks -- the inference this column
            // replaces, made once. A stand-in has no identity and no address but a name, and
            // is never a member; a deleted account has no identity and nothing left at all.
            // Everything else is an account. A member with no identity is still an account:
            // somebody who has not signed in yet, not a placeholder.
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

            migrationBuilder.AddColumn<Guid>(
                name: "ProvisionedForUserId",
                table: "SplitRule",
                type: "uuid",
                nullable: true);

            // Whose each built-in "all for one" rule is, from its open version. Where a
            // person somehow has two -- the race the new index closes -- the oldest stays
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

            // The history so far, as far as it can be known: each membership's latest start
            // -- the only one a reopened row remembers -- and its departure where it has one.
            migrationBuilder.Sql(
                """
                INSERT INTO "GroupMembershipEvent" ("Id", "GroupId", "UserId", "Kind", "At", "ByUserId")
                SELECT gen_random_uuid(), m."GroupsId", m."UsersId", 'Joined', m."JoinedAt", m."UsersId"
                FROM "GroupUser" m;

                INSERT INTO "GroupMembershipEvent" ("Id", "GroupId", "UserId", "Kind", "At", "ByUserId")
                SELECT gen_random_uuid(), m."GroupsId", m."UsersId", m."LeftReason", m."LeftAt", m."LeftByUserId"
                FROM "GroupUser" m
                WHERE m."LeftAt" IS NOT NULL;
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
            migrationBuilder.DropForeignKey(
                name: "FK_Category_SplitRule_DefaultSplitRuleId",
                table: "Category");

            migrationBuilder.DropForeignKey(
                name: "FK_Group_SplitRule_DefaultSplitRuleId",
                table: "Group");

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
