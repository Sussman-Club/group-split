using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddBuiltInEvenSplitRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultSplitRuleId",
                table: "Group",
                type: "uuid",
                nullable: true);

            // The even division used to be an unnamed fallback. Give every existing group a
            // protected, named copy before making it the persisted fallback, so clients can
            // choose it explicitly and old groups have the same built-ins as new ones.
            migrationBuilder.Sql(
                """
                WITH wanting AS (
                    SELECT g."Id" AS group_id,
                           gen_random_uuid() AS rule_id,
                           gen_random_uuid() AS version_id,
                           CASE
                               WHEN EXISTS (SELECT 1 FROM "SplitRule" r
                                            WHERE r."GroupId" = g."Id"
                                              AND lower(r."Name") = lower('Even'))
                               THEN 'Even (' || left(g."Id"::text, 8) || ')'
                               ELSE 'Even'
                           END AS name
                    FROM "Group" g
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM "SplitRule" r
                        JOIN "SplitRuleVersion" v ON v."SplitRuleId" = r."Id"
                        JOIN "EvenSplitRuleVersion" ev ON ev."Id" = v."Id"
                        WHERE r."GroupId" = g."Id"
                          AND r."BuiltIn"
                          AND v."SupersededAt" IS NULL)
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
                ),
                created_weighted AS (
                    INSERT INTO "WeightedSplitRuleVersion" ("Id")
                    SELECT version_id FROM wanting
                    RETURNING "Id"
                )
                INSERT INTO "EvenSplitRuleVersion" ("Id")
                SELECT version_id FROM wanting;

                UPDATE "Group" g
                SET "DefaultSplitRuleId" = r."Id"
                FROM "SplitRule" r
                JOIN "SplitRuleVersion" v ON v."SplitRuleId" = r."Id"
                JOIN "EvenSplitRuleVersion" ev ON ev."Id" = v."Id"
                WHERE r."GroupId" = g."Id"
                  AND r."BuiltIn"
                  AND v."SupersededAt" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Group_DefaultSplitRuleId",
                table: "Group",
                column: "DefaultSplitRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Group_SplitRule_DefaultSplitRuleId",
                table: "Group",
                column: "DefaultSplitRuleId",
                principalTable: "SplitRule",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Group_SplitRule_DefaultSplitRuleId",
                table: "Group");

            migrationBuilder.DropIndex(
                name: "IX_Group_DefaultSplitRuleId",
                table: "Group");

            migrationBuilder.DropColumn(
                name: "DefaultSplitRuleId",
                table: "Group");
        }
    }
}
