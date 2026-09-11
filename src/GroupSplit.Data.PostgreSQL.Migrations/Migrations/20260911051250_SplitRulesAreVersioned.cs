using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Splits a rule into the name a category points at and the versions it has been
    /// through, and records on every transaction which version divided it.
    /// </summary>
    /// <remarks>
    /// Nothing is deleted and no row moves table. What was one <c>SplitRule</c> row becomes
    /// two: the rule keeps its id, its group and its name, and its division becomes the
    /// first version of it.
    /// <para>
    /// The version takes the rule's own id. That is deliberate and it is what keeps this
    /// migration cheap: the participants already point at that id, so their column is
    /// renamed rather than rewritten, and <c>Category.DefaultSplitRuleId</c> already holds
    /// it, so every expense can be pointed at the version that divided it with one update
    /// and no mapping table. From the second version onwards ids are ordinary and unrelated.
    /// </para>
    /// <para>
    /// Additive first, then the backfill, then the drops -- so a failure part-way leaves a
    /// database that still reads.
    /// </para>
    /// <para>
    /// What the backfill claims, and what it cannot know: an expense filed under a category
    /// is pointed at the version of that category's rule, because that is the division the
    /// splitter applied when the expense was written. An expense whose shares somebody typed
    /// in by hand is pointed there too, since nothing recorded the difference -- its stored
    /// splits are unchanged and remain the record of what each person actually owed, so what
    /// is lost is only that a later recalculation would divide it by the category's rule
    /// rather than by the shares originally typed. Going forward the two are told apart,
    /// because a stated split now writes no version at all.
    /// </para>
    /// </remarks>
    public partial class SplitRulesAreVersioned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SplitRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SplitRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Discriminator = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SplitRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SplitRuleVersion_SplitRule_SplitRuleId",
                        column: x => x.SplitRuleId,
                        principalTable: "SplitRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // One version per rule, carrying the rule's own id and its kind. Every existing
            // rule has said one thing since it was created, and that is what a chain of one
            // version means.
            //
            // The kind is renamed on the way: the classes are SplitRuleVersion subtypes now,
            // and EF's discriminator is the class name. Nothing else about them changed.
            migrationBuilder.Sql("""
                INSERT INTO "SplitRuleVersion" ("Id", "SplitRuleId", "StartedAt", "SupersededAt", "Discriminator")
                SELECT sr."Id", sr."Id", now(), NULL, sr."Discriminator" || 'Version'
                FROM "SplitRule" AS sr;
                """);

            // The participants pointed at the rule and now point at its first version, which
            // has the same id -- so the values are already right and only the column's name
            // and its foreign key change.
            migrationBuilder.DropForeignKey(
                name: "FK_SplitRuleParticipant_SplitRule_SplitRuleId",
                table: "SplitRuleParticipant");

            migrationBuilder.RenameColumn(
                name: "SplitRuleId",
                table: "SplitRuleParticipant",
                newName: "SplitRuleVersionId");

            migrationBuilder.RenameIndex(
                name: "IX_SplitRuleParticipant_SplitRuleId_UserId",
                table: "SplitRuleParticipant",
                newName: "IX_SplitRuleParticipant_SplitRuleVersionId_UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_SplitRuleParticipant_SplitRuleVersion_SplitRuleVersionId",
                table: "SplitRuleParticipant",
                column: "SplitRuleVersionId",
                principalTable: "SplitRuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Which kind a rule is now lives on its versions, because that is what can
            // change: editing a percentage rule into a shares rule is a new version, not a
            // row that changed type.
            migrationBuilder.DropIndex(
                name: "IX_SplitRule_Discriminator",
                table: "SplitRule");

            migrationBuilder.DropColumn(
                name: "Discriminator",
                table: "SplitRule");

            migrationBuilder.AddColumn<Guid>(
                name: "SplitRuleVersionId",
                table: "Transaction",
                type: "uuid",
                nullable: true);

            // Nullable and left null for everything that never had a rule: a transfer, a
            // personal expense, an expense filed under nothing or under a category that
            // names no rule. Those divide the way they always have -- to the recipient, to
            // the payer, or evenly -- and an invented row would say otherwise.
            migrationBuilder.Sql("""
                UPDATE "Transaction" AS t
                SET "SplitRuleVersionId" = c."DefaultSplitRuleId"
                FROM "Category" AS c
                WHERE t."CategoryId" = c."Id" AND c."DefaultSplitRuleId" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_SplitRuleVersionId",
                table: "Transaction",
                column: "SplitRuleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRuleVersion_Discriminator",
                table: "SplitRuleVersion",
                column: "Discriminator");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRuleVersion_SplitRuleId_Current",
                table: "SplitRuleVersion",
                column: "SplitRuleId",
                unique: true,
                filter: "\"SupersededAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SplitRuleVersion_SplitRuleId_SupersededAt",
                table: "SplitRuleVersion",
                columns: new[] { "SplitRuleId", "SupersededAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_SplitRuleVersion_SplitRuleVersionId",
                table: "Transaction",
                column: "SplitRuleVersionId",
                principalTable: "SplitRuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        /// <summary>
        /// Folds the current version back into the rule it belongs to, and refuses when
        /// there is history that would be lost by doing so.
        /// </summary>
        /// <remarks>
        /// A rule that has never been edited goes back exactly as it came: one version,
        /// carrying the rule's own id, whose kind and participants are the rule's again. A
        /// rule that has been edited has versions the old shape cannot hold -- one row, one
        /// division -- and reverting would silently discard every division but the latest,
        /// along with the record of which one each expense was divided by. That is refused
        /// rather than done quietly.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    edited integer;
                BEGIN
                    SELECT count(*) INTO edited FROM "SplitRuleVersion" WHERE "SupersededAt" IS NOT NULL;

                    IF edited > 0 THEN
                        RAISE EXCEPTION
                            'SplitRulesAreVersioned cannot be reverted: % split rule version(s) have been superseded, and a rule without versions has nowhere to put them.',
                            edited;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_SplitRuleVersion_SplitRuleVersionId",
                table: "Transaction");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_SplitRuleVersionId",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "SplitRuleVersionId",
                table: "Transaction");

            migrationBuilder.AddColumn<string>(
                name: "Discriminator",
                table: "SplitRule",
                type: "character varying(21)",
                maxLength: 21,
                nullable: false,
                defaultValue: "");

            // Back off the one remaining version of each rule, with the suffix the forward
            // direction added taken off again.
            migrationBuilder.Sql("""
                UPDATE "SplitRule" AS sr
                SET "Discriminator" = left(srv."Discriminator", length(srv."Discriminator") - length('Version'))
                FROM "SplitRuleVersion" AS srv
                WHERE srv."SplitRuleId" = sr."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SplitRule_Discriminator",
                table: "SplitRule",
                column: "Discriminator");

            migrationBuilder.DropForeignKey(
                name: "FK_SplitRuleParticipant_SplitRuleVersion_SplitRuleVersionId",
                table: "SplitRuleParticipant");

            // The version ids are the rule ids, which is what makes this a rename back
            // rather than a rewrite -- and what the guard above is protecting.
            migrationBuilder.RenameColumn(
                name: "SplitRuleVersionId",
                table: "SplitRuleParticipant",
                newName: "SplitRuleId");

            migrationBuilder.RenameIndex(
                name: "IX_SplitRuleParticipant_SplitRuleVersionId_UserId",
                table: "SplitRuleParticipant",
                newName: "IX_SplitRuleParticipant_SplitRuleId_UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_SplitRuleParticipant_SplitRule_SplitRuleId",
                table: "SplitRuleParticipant",
                column: "SplitRuleId",
                principalTable: "SplitRule",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropTable(
                name: "SplitRuleVersion");
        }
    }
}
