using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Deletes the old model: the rule-version hierarchy, its participant tables and
    /// <c>Rule</c> itself. What they said is carried over first -- each rule becomes a
    /// category, its latest division becomes the split rule that category defaults to, and
    /// every expense is filed under the category made from the rule it used to point at.
    /// </summary>
    /// <remarks>
    /// One-way, like the reshape before it. That is safe only because the only data is seed
    /// and development data; a database with real history would need a plan this does not
    /// attempt, and the comment is here so the next person meets it.
    /// <para>
    /// Ids are reused rather than generated so that the seeder recognises what it finds: a
    /// category takes the id of the rule it replaces, which is also the id the seed
    /// transactions name, and a split rule takes the id of the version it was read from.
    /// </para>
    /// <para>
    /// Additive first, then the re-pointing, then the drops -- so a failure part-way leaves
    /// a database that still reads.
    /// </para>
    /// </remarks>
    public partial class DropRuleHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column is about to hold category ids, so it cannot keep pointing at
            // rule versions while it does.
            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_RuleVersion_RuleVersionId",
                table: "Transaction");

            migrationBuilder.RenameColumn(
                name: "RuleVersionId",
                table: "Transaction",
                newName: "CategoryId");

            migrationBuilder.RenameIndex(
                name: "IX_Transaction_RuleVersionId",
                table: "Transaction",
                newName: "IX_Transaction_CategoryId");

            Backfill(migrationBuilder);

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_Category_CategoryId",
                table: "Transaction",
                column: "CategoryId",
                principalTable: "Category",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropTable(
                name: "PercentRuleUser");

            migrationBuilder.DropTable(
                name: "PersonalRuleVersion");

            migrationBuilder.DropTable(
                name: "SettlementRuleVersion");

            migrationBuilder.DropTable(
                name: "SharesRuleUser");

            migrationBuilder.DropTable(
                name: "SharesRuleVersion");

            migrationBuilder.DropTable(
                name: "PercentRuleVersion");

            migrationBuilder.DropTable(
                name: "RuleVersion");

            migrationBuilder.DropTable(
                name: "Rule");
        }

        /// <summary>
        /// Carries the rules over as categories and split rules, then files every expense
        /// under the category made from its rule.
        /// </summary>
        /// <remarks>
        /// A rule was the label and the split at once. The label becomes a
        /// <c>Category</c>; the split, read from the rule's latest version, becomes the
        /// <c>SplitRule</c> the category defaults to. Rules in a personal group -- the
        /// locked "Default" one every user carried -- become nothing: a personal expense is
        /// filed under no category and divides with nobody.
        /// <para>
        /// A percentage is stored in hundredths from here on, so 33.33 becomes 3333. That
        /// rounding happens once, here, the way it happens once at the API's edge; a set of
        /// percentages that no longer sums to exactly 100 after it will be refused the next
        /// time somebody edits the rule, which is the first moment anything could act on it.
        /// </para>
        /// </remarks>
        private static void Backfill(MigrationBuilder migrationBuilder)
        {
            // The label. Every rule outside a personal group, whatever its division, so
            // that no expense loses the name it was filed under.
            migrationBuilder.Sql("""
                INSERT INTO "Category" ("Id", "GroupId", "Name", "DefaultSplitRuleId")
                SELECT r."Id", r."GroupId", r."Category", NULL
                FROM "Rule" AS r
                WHERE NOT EXISTS (SELECT 1 FROM "User" AS u WHERE u."PersonalGroupId" = r."GroupId")
                ON CONFLICT DO NOTHING;
                """);

            // The split: the latest version of each rule that had a proportional one. A
            // shares version is a percent version underneath, so it is told apart by its
            // own table, and only its own participants are read -- the percentages it
            // also wrote were a conversion, and the conversion is where the rounding bug
            // lived.
            migrationBuilder.Sql("""
                WITH latest AS (
                    SELECT DISTINCT ON (rv."RuleId") rv."RuleId", rv."Id"
                    FROM "RuleVersion" AS rv
                    ORDER BY rv."RuleId", (rv."EndDateTime" IS NULL) DESC, rv."StartDateTime" DESC
                )
                INSERT INTO "SplitRule" ("Id", "GroupId", "Name", "Discriminator")
                SELECT l."Id",
                       r."GroupId",
                       r."Category",
                       CASE WHEN EXISTS (SELECT 1 FROM "SharesRuleVersion" AS s WHERE s."Id" = l."Id")
                            THEN 'SharesSplitRule'
                            ELSE 'PercentSplitRule'
                       END
                FROM latest AS l
                JOIN "Rule" AS r ON r."Id" = l."RuleId"
                JOIN "Category" AS c ON c."Id" = r."Id"
                WHERE EXISTS (SELECT 1 FROM "PercentRuleVersion" AS p WHERE p."Id" = l."Id")
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.Sql("""
                INSERT INTO "SplitRuleParticipant" ("Id", "SplitRuleId", "UserId", "Weight")
                SELECT gen_random_uuid(), sr."Id", su."UserId", su."Shares"
                FROM "SplitRule" AS sr
                JOIN "SharesRuleUser" AS su ON su."RuleVersionId" = sr."Id"
                WHERE sr."Discriminator" = 'SharesSplitRule';
                """);

            migrationBuilder.Sql("""
                INSERT INTO "SplitRuleParticipant" ("Id", "SplitRuleId", "UserId", "Weight")
                SELECT gen_random_uuid(), sr."Id", pu."UserId", CAST(round(pu."Percentage" * 100) AS integer)
                FROM "SplitRule" AS sr
                JOIN "PercentRuleUser" AS pu ON pu."RuleVersionId" = sr."Id"
                WHERE sr."Discriminator" = 'PercentSplitRule';
                """);

            // The category points at the rule named after it, which is the pair the seeder
            // writes too.
            migrationBuilder.Sql("""
                UPDATE "Category" AS c
                SET "DefaultSplitRuleId" = sr."Id"
                FROM "SplitRule" AS sr
                WHERE sr."GroupId" = c."GroupId" AND sr."Name" = c."Name";
                """);

            // The column still holds rule-version ids. Each becomes the id of the category
            // made from that version's rule, or null where none was made -- a personal
            // expense, or a transfer, which never had one.
            migrationBuilder.Sql("""
                UPDATE "Transaction" AS t
                SET "CategoryId" = (SELECT c."Id"
                                    FROM "RuleVersion" AS rv
                                    JOIN "Category" AS c ON c."Id" = rv."RuleId"
                                    WHERE rv."Id" = t."CategoryId");
                """);
        }

        /// <inheritdoc />
        /// <summary>
        /// Puts the tables back, and refuses when the rows cannot follow them.
        /// </summary>
        /// <remarks>
        /// The categories and split rules made going forward are left where they are, but
        /// an expense filed under a category has no rule version to be put back onto, and
        /// the old foreign key would fail later and less clearly. A database with nothing
        /// filed goes back cleanly.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    filed integer;
                BEGIN
                    SELECT count(*) INTO filed FROM "Transaction" WHERE "CategoryId" IS NOT NULL;

                    IF filed > 0 THEN
                        RAISE EXCEPTION
                            'DropRuleHierarchy cannot be reverted: % expense(s) are filed under a category, and the rule versions they would point at were deleted going forward.',
                            filed;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_Category_CategoryId",
                table: "Transaction");

            migrationBuilder.RenameColumn(
                name: "CategoryId",
                table: "Transaction",
                newName: "RuleVersionId");

            migrationBuilder.RenameIndex(
                name: "IX_Transaction_CategoryId",
                table: "Transaction",
                newName: "IX_Transaction_RuleVersionId");

            migrationBuilder.CreateTable(
                name: "Rule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Flags = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rule_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleVersion_Rule_RuleId",
                        column: x => x.RuleId,
                        principalTable: "Rule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PercentRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PercentRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PercentRuleVersion_RuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "RuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonalRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonalRuleVersion_RuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "RuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SettlementRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OtherUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettlementRuleVersion_RuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "RuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SettlementRuleVersion_User_OtherUserId",
                        column: x => x.OtherUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PercentRuleUser",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Percentage = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PercentRuleUser", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PercentRuleUser_PercentRuleVersion_RuleVersionId",
                        column: x => x.RuleVersionId,
                        principalTable: "PercentRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PercentRuleUser_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharesRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharesRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharesRuleVersion_PercentRuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "PercentRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharesRuleUser",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Shares = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharesRuleUser", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharesRuleUser_SharesRuleVersion_RuleVersionId",
                        column: x => x.RuleVersionId,
                        principalTable: "SharesRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SharesRuleUser_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PercentRuleUser_RuleVersionId",
                table: "PercentRuleUser",
                column: "RuleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PercentRuleUser_UserId_RuleVersionId",
                table: "PercentRuleUser",
                columns: new[] { "UserId", "RuleVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rule_Category",
                table: "Rule",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Rule_GroupId_Category",
                table: "Rule",
                columns: new[] { "GroupId", "Category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuleVersion_RuleId",
                table: "RuleVersion",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementRuleVersion_OtherUserId",
                table: "SettlementRuleVersion",
                column: "OtherUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SharesRuleUser_RuleVersionId",
                table: "SharesRuleUser",
                column: "RuleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SharesRuleUser_UserId_RuleVersionId",
                table: "SharesRuleUser",
                columns: new[] { "UserId", "RuleVersionId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_RuleVersion_RuleVersionId",
                table: "Transaction",
                column: "RuleVersionId",
                principalTable: "RuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
