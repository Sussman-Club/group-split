using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Empties the personal group out and takes it away, and gives a group somewhere to
    /// keep the people it has asked to join.
    /// </summary>
    /// <remarks>
    /// Every account was provisioned with a hidden group called "Personal" so that an
    /// expense of one's own had a group to belong to -- a requirement that went when
    /// <c>Transaction.GroupId</c> became nullable. What was left was a group of one that
    /// still counted as a group: it appeared in the switcher, made a card on the home page,
    /// and made everyone's group count one too high. A personal expense is now one with no
    /// group, which is what it always was.
    /// <para>
    /// The carry-over runs before the column goes, because it is the column that says which
    /// group was whose. Splits on a personal expense go with it: the only share was the
    /// payer's own, and nothing sums balances outside a group.
    /// </para>
    /// <para>
    /// One-way, like the reshape before it, and safe for the same reason -- the only data is
    /// seed and development data. <c>Down</c> restores the shape and not the groups; there
    /// is nothing left to restore them from.
    /// </para>
    /// </remarks>
    public partial class PersonalLedgerAndInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CarryPersonalTransactionsOut(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_User_Group_PersonalGroupId",
                table: "User");

            migrationBuilder.DropIndex(
                name: "IX_User_PersonalGroupId",
                table: "User");

            migrationBuilder.DropColumn(
                name: "PersonalGroupId",
                table: "User");

            migrationBuilder.Sql("""
                DELETE FROM "Group" g
                USING "PersonalGroupIds" p
                WHERE g."Id" = p."Id";
                """);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "JoinedAt",
                table: "GroupUser",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            // Every membership that exists at this moment predates the column, and there are
            // few enough of them to date outright rather than leave the column nullable and
            // make every reader downstream carry a case that only ever meant "early". They
            // get the day the app started keeping track.
            migrationBuilder.Sql("""
                UPDATE "GroupUser" SET "JoinedAt" = TIMESTAMPTZ '2026-09-04 00:00:00+00';
                """);

            migrationBuilder.CreateTable(
                name: "GroupInvitation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvitedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupInvitation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupInvitation_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupInvitation_User_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_Email",
                table: "GroupInvitation",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_GroupId_Email",
                table: "GroupInvitation",
                columns: new[] { "GroupId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_InvitedByUserId",
                table: "GroupInvitation",
                column: "InvitedByUserId");
        }

        /// <summary>
        /// Takes every transaction out of its owner's personal group, drops the shares that
        /// only ever named the owner, and deletes the groups themselves.
        /// </summary>
        /// <remarks>
        /// Written against the column rather than against the group's name: "Personal" is a
        /// name anybody could have given a real group, and deleting one of those would take
        /// its members' history with it. <c>User.PersonalGroupId</c> is the only thing that
        /// actually says which group was provisioned rather than made.
        /// </remarks>
        private static void CarryPersonalTransactionsOut(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "TransactionSplit" s
                USING "Transaction" t
                WHERE s."TransactionId" = t."Id"
                  AND t."GroupId" IN (SELECT "PersonalGroupId" FROM "User");
                """);

            migrationBuilder.Sql("""
                UPDATE "Transaction"
                SET "GroupId" = NULL
                WHERE "GroupId" IN (SELECT "PersonalGroupId" FROM "User");
                """);

            // Categories and rules first: a personal group could not be given either through
            // the app, but a database that has one would otherwise refuse the delete below
            // and say nothing useful about why.
            migrationBuilder.Sql("""
                DELETE FROM "SplitRuleParticipant" p
                USING "SplitRule" r
                WHERE p."SplitRuleId" = r."Id"
                  AND r."GroupId" IN (SELECT "PersonalGroupId" FROM "User");
                """);

            migrationBuilder.Sql("""
                DELETE FROM "Category"
                WHERE "GroupId" IN (SELECT "PersonalGroupId" FROM "User");
                """);

            migrationBuilder.Sql("""
                DELETE FROM "SplitRule"
                WHERE "GroupId" IN (SELECT "PersonalGroupId" FROM "User");
                """);

            migrationBuilder.Sql("""
                DELETE FROM "GroupUser"
                WHERE "GroupsId" IN (SELECT "PersonalGroupId" FROM "User");
                """);

            // Last, and through a temporary table: the rows naming these groups are gone,
            // but the column pointing at them has not been dropped yet, so the delete has to
            // outlive the reference it is about to make dangle.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "PersonalGroupIds" ON COMMIT DROP AS
                SELECT "PersonalGroupId" AS "Id" FROM "User";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupInvitation");

            migrationBuilder.DropColumn(
                name: "JoinedAt",
                table: "GroupUser");

            migrationBuilder.AddColumn<Guid>(
                name: "PersonalGroupId",
                table: "User",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_User_PersonalGroupId",
                table: "User",
                column: "PersonalGroupId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_User_Group_PersonalGroupId",
                table: "User",
                column: "PersonalGroupId",
                principalTable: "Group",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
