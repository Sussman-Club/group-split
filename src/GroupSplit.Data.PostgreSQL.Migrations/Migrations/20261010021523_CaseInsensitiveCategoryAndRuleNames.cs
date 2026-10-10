using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// Category and split rule names are unique in a group whatever their case.
    /// </summary>
    /// <remarks>
    /// The services already refused "food" beside "Food", but the indexes compared names as
    /// written, so two requests at once both got in -- and "Food" twice answered a 500. The
    /// indexes keep their names, which the API maps a violation by, and move onto
    /// lower("Name"): the same comparison the services make with ToLower. The model cannot
    /// express an index on an expression, so it still describes them as plain (GroupId, Name)
    /// indexes; nothing regenerates them unless that declaration changes.
    /// <para>
    /// Rows that already differ only in case would stop the unique index being built, so
    /// they are renamed first rather than the migration failing: the first of each set --
    /// built-in rules first, then by byte order, capitals first, so "Food" keeps its name
    /// over "food", then by id -- keeps it, and the rest take the next free " (n)" in that
    /// order, as a member's rule does when the plain name is taken. Each rename is reported as a NOTICE in the migration's
    /// output. Nothing is merged: expenses keep the category they were filed under, which is
    /// now the one with the suffix. Not undone by Down.
    /// </para>
    /// <para>
    /// The concurrency token on a membership's LeftAt is model-only: it changes the WHERE of
    /// EF's updates, not the table, so it has nothing to migrate.
    /// </para>
    /// </remarks>
    public partial class CaseInsensitiveCategoryAndRuleNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RenameCaseClashes("Category", "\"Name\" COLLATE \"C\", \"Id\""));
            migrationBuilder.Sql(RenameCaseClashes("SplitRule", "\"BuiltIn\" DESC, \"Name\" COLLATE \"C\", \"Id\""));

            migrationBuilder.DropIndex(
                name: "IX_Category_GroupId_Name",
                table: "Category");

            migrationBuilder.DropIndex(
                name: "IX_SplitRule_GroupId_Name",
                table: "SplitRule");

            migrationBuilder.Sql(
                """CREATE UNIQUE INDEX "IX_Category_GroupId_Name" ON "Category" ("GroupId", lower("Name"));""");

            migrationBuilder.Sql(
                """CREATE UNIQUE INDEX "IX_SplitRule_GroupId_Name" ON "SplitRule" ("GroupId", lower("Name"));""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Category_GroupId_Name",
                table: "Category");

            migrationBuilder.DropIndex(
                name: "IX_SplitRule_GroupId_Name",
                table: "SplitRule");

            migrationBuilder.CreateIndex(
                name: "IX_Category_GroupId_Name",
                table: "Category",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SplitRule_GroupId_Name",
                table: "SplitRule",
                columns: new[] { "GroupId", "Name" },
                unique: true);
        }

        /// <summary>
        /// Renames every name in <paramref name="table"/> that another in its group already
        /// holds in some other case, keeping the first by <paramref name="keeperOrder"/>.
        /// </summary>
        /// <remarks>
        /// 64 is both columns' length, and the suffix has to fit inside it. Each candidate is
        /// checked against the table as it stands, renames included, so a "Food (2)" that was
        /// already there is stepped over rather than collided with.
        /// </remarks>
        private static string RenameCaseClashes(string table, string keeperOrder) =>
            $$"""
              DO $$
              DECLARE
                  clash record;
                  suffix int;
                  candidate text;
              BEGIN
                  FOR clash IN
                      SELECT "Id", "GroupId", "Name"
                      FROM (
                          SELECT "Id", "GroupId", "Name",
                                 row_number() OVER (PARTITION BY "GroupId", lower("Name") ORDER BY {{keeperOrder}}) AS place
                          FROM "{{table}}"
                      ) ranked
                      WHERE place > 1
                      ORDER BY "GroupId", lower("Name"), place
                  LOOP
                      suffix := 2;

                      LOOP
                          candidate := left(clash."Name", 64 - length(' (' || suffix || ')')) || ' (' || suffix || ')';

                          EXIT WHEN NOT EXISTS (
                              SELECT 1 FROM "{{table}}"
                              WHERE "GroupId" = clash."GroupId" AND lower("Name") = lower(candidate));

                          suffix := suffix + 1;
                      END LOOP;

                      RAISE NOTICE '{{table}} %: "%" renamed to "%", another in its group differing only in case',
                          clash."Id", clash."Name", candidate;

                      UPDATE "{{table}}" SET "Name" = candidate WHERE "Id" = clash."Id";
                  END LOOP;
              END $$;
              """;
    }
}
