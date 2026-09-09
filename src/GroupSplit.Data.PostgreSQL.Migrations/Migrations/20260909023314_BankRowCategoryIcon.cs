using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// The mark a row falls back to when its merchant has no logo, or it named no merchant.
    /// </summary>
    /// <remarks>
    /// Backfilled from the stored payload rather than left to the next sync. Plaid sends
    /// this on every row and sends a merchant logo only for merchants it recognises -- never
    /// in the sandbox -- so without the backfill every row already imported would keep
    /// showing two initials until its account happened to be synced again.
    /// </remarks>
    public partial class BankRowCategoryIcon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CategoryIconUrl",
                table: "BankTransaction",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            // Plaid's field name, in a migration, on purpose: this is a one-off reading of
            // history that a particular provider wrote, not something the app does at
            // runtime. A row from anywhere else, or a seeded one whose payload is "{}",
            // matches nothing and stays null -- which is what having no icon looks like.
            migrationBuilder.Sql(
                """
                UPDATE "BankTransaction"
                SET "CategoryIconUrl" = left("RawJson"::jsonb->>'personal_finance_category_icon_url', 512)
                WHERE "RawJson"::jsonb->>'personal_finance_category_icon_url' IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CategoryIconUrl",
                table: "BankTransaction");
        }
    }
}
