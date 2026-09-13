using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// The rule each group is given per member -- all of it is for them -- and the reshape
    /// that makes room for it: a table per kind of version.
    /// </summary>
    /// <remarks>
    /// The order is the whole of the risk. The per-kind tables are created; every version
    /// that already exists is given its row in the right one, read off the discriminator that
    /// is about to be dropped; only then does the discriminator go, and only after it has can
    /// a version be written that has nothing to put in it.
    /// <para>
    /// One kind does not survive the crossing. "All on whoever paid" was a rule that named
    /// nobody, and the kind that replaces it names somebody by definition -- so those rules
    /// are removed rather than guessed at. The expenses divided by one keep every share they
    /// hold; what they lose is the pointer to the division that produced them, which is the
    /// same thing a hand-typed split records. A category defaulting to one is left defaulting
    /// to nothing, which divides evenly -- what that category has to say about who owes is
    /// then said by whoever records the expense.
    /// </para>
    /// <para>
    /// Nothing else here touches a stored share, and every surviving version keeps its id, so
    /// every other expense goes on pointing at the division that produced its amounts.
    /// </para>
    /// </remarks>
    public partial class AllForOneMemberSplitRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SplitRuleParticipant_SplitRuleVersion_SplitRuleVersionId",
                table: "SplitRuleParticipant");

            migrationBuilder.AddColumn<bool>(
                name: "BuiltIn",
                table: "SplitRule",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SoleSplitRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoleSplitRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoleSplitRuleVersion_SplitRuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "SplitRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SoleSplitRuleVersion_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WeightedSplitRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeightedSplitRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeightedSplitRuleVersion_SplitRuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "SplitRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvenSplitRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvenSplitRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvenSplitRuleVersion_WeightedSplitRuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "WeightedSplitRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PercentSplitRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PercentSplitRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PercentSplitRuleVersion_WeightedSplitRuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "WeightedSplitRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharesSplitRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharesSplitRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharesSplitRuleVersion_WeightedSplitRuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "WeightedSplitRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SoleSplitRuleVersion_UserId",
                table: "SoleSplitRuleVersion",
                column: "UserId");

            // The kind that named nobody, out first: what it said cannot be said by the kind
            // that replaces it, and nothing here can invent the person it would need. The
            // shares stay exactly as they are on every expense one of these divided.
            migrationBuilder.Sql(
                """
                UPDATE "Transaction" SET "SplitRuleVersionId" = NULL
                WHERE "SplitRuleVersionId" IN (
                    SELECT "Id" FROM "SplitRuleVersion" WHERE "Discriminator" = 'PayerSplitRuleVersion');

                DELETE FROM "SplitRuleVersion" WHERE "Discriminator" = 'PayerSplitRuleVersion';

                UPDATE "Category" SET "DefaultSplitRuleId" = NULL
                WHERE "DefaultSplitRuleId" IN (
                    SELECT r."Id" FROM "SplitRule" r
                    WHERE NOT EXISTS (SELECT 1 FROM "SplitRuleVersion" v WHERE v."SplitRuleId" = r."Id"));

                DELETE FROM "SplitRule" r
                WHERE NOT EXISTS (SELECT 1 FROM "SplitRuleVersion" v WHERE v."SplitRuleId" = r."Id");
                """);

            // Every version that is left, into the table for the kind it has always been.
            // The proportional ones get two rows -- the middle layer holds the participants,
            // and the leaf says which way their numbers read -- which is the shape rather
            // than duplication: each table holds what that level of the hierarchy has.
            //
            // This step cannot be skipped or reordered. After the drop below there is nothing
            // left to read a kind from, and a version with no leaf row is a version of no
            // kind at all.
            migrationBuilder.Sql(
                """
                INSERT INTO "WeightedSplitRuleVersion" ("Id")
                SELECT "Id" FROM "SplitRuleVersion"
                WHERE "Discriminator" IN (
                    'EvenSplitRuleVersion', 'PercentSplitRuleVersion', 'SharesSplitRuleVersion');

                INSERT INTO "EvenSplitRuleVersion" ("Id")
                SELECT "Id" FROM "SplitRuleVersion" WHERE "Discriminator" = 'EvenSplitRuleVersion';

                INSERT INTO "PercentSplitRuleVersion" ("Id")
                SELECT "Id" FROM "SplitRuleVersion" WHERE "Discriminator" = 'PercentSplitRuleVersion';

                INSERT INTO "SharesSplitRuleVersion" ("Id")
                SELECT "Id" FROM "SplitRuleVersion" WHERE "Discriminator" = 'SharesSplitRuleVersion';
                """);

            // And now the column that said which kind a version was, which every one of them
            // says for itself. Before the backfill below and not after: a version written
            // while the column is still there has to say something in it, and what an insert
            // into a table-per-kind says is nothing.
            migrationBuilder.DropIndex(
                name: "IX_SplitRuleVersion_Discriminator",
                table: "SplitRuleVersion");

            migrationBuilder.DropColumn(
                name: "Discriminator",
                table: "SplitRuleVersion");

            // Every group that already exists gets what a new one is now given as it is
            // created: one rule per member, putting the whole of an expense on them. Written
            // here rather than left to the next join, because the expense dialog offers the
            // people rather than a list of rules -- a member who never rejoins would
            // otherwise be the one person nothing could be recorded as being for.
            //
            // Names are unique within a group, and neither a group with two members called
            // Ana nor one that has already written a rule called "All for Ana" is a reason to
            // refuse the backfill. Those get the head of the member's id after the name,
            // which is ugly, rare, and readable enough -- the app names these by the member
            // rather than by the name at all.
            //
            // MATERIALIZED because the ids are generated in it and read three times: without
            // it PostgreSQL is free to inline the query and generate a fresh set for each
            // reader, which would leave every rule holding nothing and every version hanging
            // off a rule that does not exist.
            migrationBuilder.Sql(
                """
                WITH candidate AS (
                    SELECT m."GroupsId" AS group_id,
                           m."UsersId"  AS user_id,
                           left('All for ' || COALESCE(
                                NULLIF(btrim(COALESCE(u."FirstName", '') || ' ' || COALESCE(u."LastName", '')), ''),
                                u."Email",
                                'one member'), 49) AS stem
                    FROM "GroupUser" m
                    JOIN "User" u ON u."Id" = m."UsersId"
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM "SoleSplitRuleVersion" s
                        JOIN "SplitRuleVersion" v ON v."Id" = s."Id"
                        JOIN "SplitRule" r ON r."Id" = v."SplitRuleId"
                        WHERE r."GroupId" = m."GroupsId"
                          AND r."BuiltIn"
                          AND v."SupersededAt" IS NULL
                          AND s."UserId" = m."UsersId")
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

            migrationBuilder.AddForeignKey(
                name: "FK_SplitRuleParticipant_WeightedSplitRuleVersion_SplitRuleVers~",
                table: "SplitRuleParticipant",
                column: "SplitRuleVersionId",
                principalTable: "WeightedSplitRuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SplitRuleParticipant_WeightedSplitRuleVersion_SplitRuleVers~",
                table: "SplitRuleParticipant");

            // The rules this migration wrote: nothing else in the group ever pointed at
            // them, and there is no flag left to tell them apart once the column below is
            // gone. Expenses that pointed at one are let go of first -- the pointer is
            // provenance, and the shares themselves are untouched, which is the most a
            // rollback can promise here. The rules it deleted on the way up do not come
            // back; a rollback cannot invent what it was right to remove.
            migrationBuilder.Sql(
                """
                UPDATE "Transaction" SET "SplitRuleVersionId" = NULL
                WHERE "SplitRuleVersionId" IN (SELECT "Id" FROM "SoleSplitRuleVersion");

                DELETE FROM "SplitRuleVersion"
                WHERE "Id" IN (SELECT "Id" FROM "SoleSplitRuleVersion");

                UPDATE "Category" SET "DefaultSplitRuleId" = NULL
                WHERE "DefaultSplitRuleId" IN (
                    SELECT r."Id" FROM "SplitRule" r
                    WHERE NOT EXISTS (SELECT 1 FROM "SplitRuleVersion" v WHERE v."SplitRuleId" = r."Id"));

                DELETE FROM "SplitRule" r
                WHERE NOT EXISTS (SELECT 1 FROM "SplitRuleVersion" v WHERE v."SplitRuleId" = r."Id");
                """);

            // Nullable first, so the rows that exist can be told what they are before the
            // column insists on it. Filled from the tables that are about to go, which is the
            // Up in reverse and for the same reason: a kind has to be somewhere at every
            // moment.
            migrationBuilder.AddColumn<string>(
                name: "Discriminator",
                table: "SplitRuleVersion",
                type: "character varying(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "SplitRuleVersion" v SET "Discriminator" = 'EvenSplitRuleVersion'
                WHERE EXISTS (SELECT 1 FROM "EvenSplitRuleVersion" l WHERE l."Id" = v."Id");

                UPDATE "SplitRuleVersion" v SET "Discriminator" = 'PercentSplitRuleVersion'
                WHERE EXISTS (SELECT 1 FROM "PercentSplitRuleVersion" l WHERE l."Id" = v."Id");

                UPDATE "SplitRuleVersion" v SET "Discriminator" = 'SharesSplitRuleVersion'
                WHERE EXISTS (SELECT 1 FROM "SharesSplitRuleVersion" l WHERE l."Id" = v."Id");

                UPDATE "SplitRuleVersion" SET "Discriminator" = 'EvenSplitRuleVersion'
                WHERE "Discriminator" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Discriminator",
                table: "SplitRuleVersion",
                type: "character varying(34)",
                maxLength: 34,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(34)",
                oldMaxLength: 34,
                oldNullable: true);

            migrationBuilder.DropTable(
                name: "EvenSplitRuleVersion");

            migrationBuilder.DropTable(
                name: "PercentSplitRuleVersion");

            migrationBuilder.DropTable(
                name: "SharesSplitRuleVersion");

            migrationBuilder.DropTable(
                name: "SoleSplitRuleVersion");

            migrationBuilder.DropTable(
                name: "WeightedSplitRuleVersion");

            migrationBuilder.DropColumn(
                name: "BuiltIn",
                table: "SplitRule");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRuleVersion_Discriminator",
                table: "SplitRuleVersion",
                column: "Discriminator");

            migrationBuilder.AddForeignKey(
                name: "FK_SplitRuleParticipant_SplitRuleVersion_SplitRuleVersionId",
                table: "SplitRuleParticipant",
                column: "SplitRuleVersionId",
                principalTable: "SplitRuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
