using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// Invitations become guests: the same rows, renamed, with the link made optional and an
    /// end recorded instead of a delete.
    /// </summary>
    /// <remarks>
    /// Renamed in place rather than dropped and recreated, which is what EF scaffolds for a
    /// renamed entity and would lose every row. Ids are kept, so an old invitation id still
    /// names the same guest.
    /// <para>
    /// Every existing invitation has a token, so each becomes a guest with a working link
    /// (<c>LinkIssuedAt = AddedAt</c>), and each gets the "GuestAdded" history row the save
    /// hook writes from now on. A stand-in still holding money with no invitation behind it
    /// -- one left by a group that closed an invitation with nobody to hand it to -- becomes a
    /// removed guest, so it reads as somebody who has gone rather than as nobody. The
    /// constraints come last, over data that already satisfies them.
    /// </para>
    /// <para>
    /// <c>Down</c> loses what the old shape cannot hold: ended guests are deleted (an ended
    /// row would read as a pending invitation again), linkless guests are given a token, and
    /// the guest history rows go.
    /// </para>
    /// </remarks>
    public partial class GuestsWithOptionalLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InvitationOpened_GroupInvitation_InvitationId",
                table: "InvitationOpened");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GroupMembershipEvent_Kind",
                table: "GroupMembershipEvent");

            migrationBuilder.RenameTable(
                name: "GroupInvitation",
                newName: "GroupGuest");

            migrationBuilder.RenameColumn(
                name: "InvitedByUserId",
                table: "GroupGuest",
                newName: "AddedByUserId");

            migrationBuilder.RenameColumn(
                name: "InvitedAt",
                table: "GroupGuest",
                newName: "AddedAt");

            migrationBuilder.Sql(
                """
                ALTER TABLE "GroupGuest" RENAME CONSTRAINT "PK_GroupInvitation" TO "PK_GroupGuest";
                ALTER TABLE "GroupGuest" RENAME CONSTRAINT "FK_GroupInvitation_Group_GroupId" TO "FK_GroupGuest_Group_GroupId";
                ALTER TABLE "GroupGuest" RENAME CONSTRAINT "FK_GroupInvitation_User_InvitedByUserId" TO "FK_GroupGuest_User_AddedByUserId";
                ALTER TABLE "GroupGuest" RENAME CONSTRAINT "FK_GroupInvitation_User_ParticipantUserId" TO "FK_GroupGuest_User_ParticipantUserId";
                """);

            migrationBuilder.RenameIndex(
                name: "IX_GroupInvitation_GroupId_ParticipantUserId",
                table: "GroupGuest",
                newName: "IX_GroupGuest_GroupId_ParticipantUserId");

            migrationBuilder.RenameIndex(
                name: "IX_GroupInvitation_InvitedByUserId",
                table: "GroupGuest",
                newName: "IX_GroupGuest_AddedByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_GroupInvitation_Token",
                table: "GroupGuest",
                newName: "IX_GroupGuest_Token");

            // Unique now: one stand-in per guest, across groups and ended rows alike.
            migrationBuilder.DropIndex(
                name: "IX_GroupInvitation_ParticipantUserId",
                table: "GroupGuest");

            migrationBuilder.CreateIndex(
                name: "IX_GroupGuest_ParticipantUserId",
                table: "GroupGuest",
                column: "ParticipantUserId",
                unique: true);

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "GroupGuest",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LinkIssuedAt",
                table: "GroupGuest",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LinkIssuedByUserId",
                table: "GroupGuest",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndedAt",
                table: "GroupGuest",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndReason",
                table: "GroupGuest",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EndedByUserId",
                table: "GroupGuest",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupGuest_LinkIssuedByUserId",
                table: "GroupGuest",
                column: "LinkIssuedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupGuest_EndedByUserId",
                table: "GroupGuest",
                column: "EndedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_GroupGuest_User_LinkIssuedByUserId",
                table: "GroupGuest",
                column: "LinkIssuedByUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_GroupGuest_User_EndedByUserId",
                table: "GroupGuest",
                column: "EndedByUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.RenameColumn(
                name: "InvitationId",
                table: "InvitationOpened",
                newName: "GuestId");

            migrationBuilder.AddForeignKey(
                name: "FK_InvitationOpened_GroupGuest_GuestId",
                table: "InvitationOpened",
                column: "GuestId",
                principalTable: "GroupGuest",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Every invitation had a link, issued when it was made, by whoever made it.
            migrationBuilder.Sql(
                """
                UPDATE "GroupGuest"
                SET "LinkIssuedAt" = "AddedAt", "LinkIssuedByUserId" = "AddedByUserId";
                """);

            // A stand-in still holding money in a group with no row behind it: a removed
            // guest, as of now, removed by nobody in particular. Its group is the one its
            // money is in; a stand-in is only ever named inside its own.
            migrationBuilder.Sql(
                """
                INSERT INTO "GroupGuest" ("Id", "GroupId", "Name", "ParticipantUserId", "AddedAt", "EndedAt", "EndReason")
                SELECT DISTINCT ON (u."Id")
                       gen_random_uuid(), held."GroupId", LEFT(COALESCE(u."FirstName", 'Guest'), 64), u."Id",
                       now(), now(), 'Removed'
                FROM "User" u
                JOIN (
                    SELECT t."UserId", t."GroupId" FROM "Transaction" t WHERE t."GroupId" IS NOT NULL
                    UNION ALL
                    SELECT s."UserId", t."GroupId"
                    FROM "TransactionSplit" s
                    JOIN "Transaction" t ON t."Id" = s."TransactionId"
                    WHERE t."GroupId" IS NOT NULL
                ) held ON held."UserId" = u."Id"
                WHERE u."Kind" = 'StandIn'
                  AND NOT EXISTS (SELECT 1 FROM "GroupGuest" g WHERE g."ParticipantUserId" = u."Id")
                ORDER BY u."Id";
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "GroupMembershipEvent" ("Id", "GroupId", "UserId", "Kind", "At", "ByUserId")
                SELECT gen_random_uuid(), g."GroupId", g."ParticipantUserId", 'GuestAdded', g."AddedAt", g."AddedByUserId"
                FROM "GroupGuest" g;

                INSERT INTO "GroupMembershipEvent" ("Id", "GroupId", "UserId", "Kind", "At", "ByUserId")
                SELECT gen_random_uuid(), g."GroupId", g."ParticipantUserId", 'GuestRemoved', g."EndedAt", NULL
                FROM "GroupGuest" g
                WHERE g."EndedAt" IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupMembershipEvent_Kind",
                table: "GroupMembershipEvent",
                sql: "\"Kind\" IN ('Joined', 'Rejoined', 'Left', 'Removed', 'AccountDeleted', 'GuestAdded', 'GuestRemoved', 'GuestClaimed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupGuest_EndReason",
                table: "GroupGuest",
                sql: "\"EndReason\" IN ('Removed', 'Claimed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupGuest_End",
                table: "GroupGuest",
                sql: "(\"EndedAt\" IS NULL) = (\"EndReason\" IS NULL) AND (\"EndedAt\" IS NOT NULL OR \"EndedByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupGuest_Link",
                table: "GroupGuest",
                sql: "(\"Token\" IS NULL) = (\"LinkIssuedAt\" IS NULL) AND (\"Token\" IS NOT NULL OR \"LinkIssuedByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupGuest_EndedNoLink",
                table: "GroupGuest",
                sql: "\"EndedAt\" IS NULL OR \"Token\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_GroupGuest_EndReason", table: "GroupGuest");
            migrationBuilder.DropCheckConstraint(name: "CK_GroupGuest_End", table: "GroupGuest");
            migrationBuilder.DropCheckConstraint(name: "CK_GroupGuest_Link", table: "GroupGuest");
            migrationBuilder.DropCheckConstraint(name: "CK_GroupGuest_EndedNoLink", table: "GroupGuest");
            migrationBuilder.DropCheckConstraint(name: "CK_GroupMembershipEvent_Kind", table: "GroupMembershipEvent");

            // What the old shape cannot hold. An ended guest would read as a pending
            // invitation again, so it goes (its opened rows cascade); a linkless guest gets a
            // token, because every invitation had one.
            migrationBuilder.Sql(
                """
                DELETE FROM "GroupMembershipEvent"
                WHERE "Kind" IN ('GuestAdded', 'GuestRemoved', 'GuestClaimed');

                DELETE FROM "GroupGuest" WHERE "EndedAt" IS NOT NULL;

                UPDATE "GroupGuest"
                SET "Token" = replace(gen_random_uuid()::text, '-', '')
                WHERE "Token" IS NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GroupMembershipEvent_Kind",
                table: "GroupMembershipEvent",
                sql: "\"Kind\" IN ('Joined', 'Rejoined', 'Left', 'Removed', 'AccountDeleted')");

            migrationBuilder.DropForeignKey(
                name: "FK_InvitationOpened_GroupGuest_GuestId",
                table: "InvitationOpened");

            migrationBuilder.RenameColumn(
                name: "GuestId",
                table: "InvitationOpened",
                newName: "InvitationId");

            migrationBuilder.DropForeignKey(
                name: "FK_GroupGuest_User_LinkIssuedByUserId",
                table: "GroupGuest");

            migrationBuilder.DropForeignKey(
                name: "FK_GroupGuest_User_EndedByUserId",
                table: "GroupGuest");

            migrationBuilder.DropIndex(
                name: "IX_GroupGuest_LinkIssuedByUserId",
                table: "GroupGuest");

            migrationBuilder.DropIndex(
                name: "IX_GroupGuest_EndedByUserId",
                table: "GroupGuest");

            migrationBuilder.DropColumn(name: "LinkIssuedAt", table: "GroupGuest");
            migrationBuilder.DropColumn(name: "LinkIssuedByUserId", table: "GroupGuest");
            migrationBuilder.DropColumn(name: "EndedAt", table: "GroupGuest");
            migrationBuilder.DropColumn(name: "EndReason", table: "GroupGuest");
            migrationBuilder.DropColumn(name: "EndedByUserId", table: "GroupGuest");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "GroupGuest",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_GroupGuest_ParticipantUserId",
                table: "GroupGuest");

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_ParticipantUserId",
                table: "GroupGuest",
                column: "ParticipantUserId");

            migrationBuilder.RenameIndex(
                name: "IX_GroupGuest_Token",
                table: "GroupGuest",
                newName: "IX_GroupInvitation_Token");

            migrationBuilder.RenameIndex(
                name: "IX_GroupGuest_AddedByUserId",
                table: "GroupGuest",
                newName: "IX_GroupInvitation_InvitedByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_GroupGuest_GroupId_ParticipantUserId",
                table: "GroupGuest",
                newName: "IX_GroupInvitation_GroupId_ParticipantUserId");

            migrationBuilder.Sql(
                """
                ALTER TABLE "GroupGuest" RENAME CONSTRAINT "PK_GroupGuest" TO "PK_GroupInvitation";
                ALTER TABLE "GroupGuest" RENAME CONSTRAINT "FK_GroupGuest_Group_GroupId" TO "FK_GroupInvitation_Group_GroupId";
                ALTER TABLE "GroupGuest" RENAME CONSTRAINT "FK_GroupGuest_User_AddedByUserId" TO "FK_GroupInvitation_User_InvitedByUserId";
                ALTER TABLE "GroupGuest" RENAME CONSTRAINT "FK_GroupGuest_User_ParticipantUserId" TO "FK_GroupInvitation_User_ParticipantUserId";
                """);

            migrationBuilder.RenameColumn(
                name: "AddedAt",
                table: "GroupGuest",
                newName: "InvitedAt");

            migrationBuilder.RenameColumn(
                name: "AddedByUserId",
                table: "GroupGuest",
                newName: "InvitedByUserId");

            migrationBuilder.RenameTable(
                name: "GroupGuest",
                newName: "GroupInvitation");

            migrationBuilder.AddForeignKey(
                name: "FK_InvitationOpened_GroupInvitation_InvitationId",
                table: "InvitationOpened",
                column: "InvitationId",
                principalTable: "GroupInvitation",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
