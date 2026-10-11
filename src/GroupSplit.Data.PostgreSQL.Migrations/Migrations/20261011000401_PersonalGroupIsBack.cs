using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Gives every account a personal group again, whose id is the account's own, moves every
    /// expense with no group into its payer's, and makes the group required.
    /// </summary>
    /// <remarks>
    /// <c>PersonalLedgerAndInvitations</c> took the hidden personal group away and made a
    /// personal expense one with no group. That cost personal spending everything that hangs
    /// off a group -- categories, a page of its own, figures -- so the group is back, this
    /// time recognised by its id rather than by a column on the user (#306).
    /// <para>
    /// Every account and every deleted one gets a group, and so does anybody else with a
    /// group-less expense, so that the column can be made required without leaving a row
    /// behind. Stand-ins do not: they never sign in and never pay for themselves.
    /// </para>
    /// <para>
    /// Expenses that went group-less in that migration had their splits deleted with it, and
    /// inside a group an expense with no split reads as money owed back to whoever paid it.
    /// Each one gets the payer's own share of the whole amount first, which is what every
    /// personal expense recorded since then already holds.
    /// </para>
    /// <para>
    /// One-way in its data, like the migrations before it. <c>Down</c> restores the shape and
    /// leaves the groups where they are; it does not take the expenses back out of them.
    /// </para>
    /// </remarks>
    public partial class PersonalGroupIsBack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CarryPersonalTransactionsIn(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_Group_GroupId",
                table: "Transaction");

            migrationBuilder.AlterColumn<Guid>(
                name: "GroupId",
                table: "Transaction",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_Group_GroupId",
                table: "Transaction",
                column: "GroupId",
                principalTable: "Group",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <summary>
        /// Makes the personal groups, puts their owners in them, gives the payer a share of
        /// every group-less expense that has none, and moves those expenses in.
        /// </summary>
        /// <remarks>
        /// <c>ON CONFLICT DO NOTHING</c> on both inserts, so an account that already has its
        /// group -- one that signed up between this build being deployed and the migration
        /// running -- is left as it is.
        /// </remarks>
        private static void CarryPersonalTransactionsIn(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "Group" ("Id", "Name", "Currency")
                SELECT u."Id", 'Personal', 'USD'
                FROM "User" u
                WHERE u."Kind" <> 'StandIn'
                   OR EXISTS (SELECT 1 FROM "Transaction" t
                              WHERE t."UserId" = u."Id" AND t."GroupId" IS NULL)
                ON CONFLICT ("Id") DO NOTHING;
                """);

            migrationBuilder.Sql("""
                INSERT INTO "GroupUser" ("GroupsId", "UsersId")
                SELECT g."Id", g."Id"
                FROM "Group" g
                JOIN "User" u ON u."Id" = g."Id"
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.Sql("""
                INSERT INTO "TransactionSplit" ("Id", "TransactionId", "UserId", "Amount")
                SELECT gen_random_uuid(), t."Id", t."UserId", t."Amount"
                FROM "Transaction" t
                WHERE t."GroupId" IS NULL
                  AND t."Discriminator" = 'Expense'
                  AND NOT EXISTS (SELECT 1 FROM "TransactionSplit" s WHERE s."TransactionId" = t."Id");
                """);

            migrationBuilder.Sql("""
                UPDATE "Transaction"
                SET "GroupId" = "UserId"
                WHERE "GroupId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_Group_GroupId",
                table: "Transaction");

            migrationBuilder.AlterColumn<Guid>(
                name: "GroupId",
                table: "Transaction",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_Group_GroupId",
                table: "Transaction",
                column: "GroupId",
                principalTable: "Group",
                principalColumn: "Id");
        }
    }
}
