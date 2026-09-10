using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// An invitation stops being an email address and becomes a named person, a single-use
    /// link, and the stand-in account the group's money is recorded against.
    /// </summary>
    /// <remarks>
    /// Three things at once because they are one change. The address was the handle, the
    /// display name and the invitee's own list all at once, and it limited a group to asking
    /// people whose email they happened to have -- which is not how most people know the
    /// friend they went on the trip with. What replaces it is a name the group chose, a token
    /// they send through whatever they actually talk on, and a row that can hold a share
    /// before anybody has an account.
    /// <para>
    /// The invitations that already exist are carried over rather than dropped. Each keeps
    /// its row, gains a stand-in named after the local part of the address it was sent to --
    /// the only handle there is -- and gains a fresh token, so the links the group has
    /// already shared are not silently made to mean something else. Nobody was ever told a
    /// personal link before now, so there is nothing to invalidate.
    /// </para>
    /// <para>
    /// A stand-in takes its invitation's own id. Deterministic, so the backfill is two plain
    /// statements rather than a correlated insert, and unique by construction -- one
    /// invitation, one stand-in, which is exactly what the unique index on
    /// (GroupId, ParticipantUserId) now says.
    /// </para>
    /// </remarks>
    public partial class PersonalInvitationLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable for the length of this migration, then filled and made required. The
            // alternative -- an empty name, a zero guid -- would be a row pointing at an
            // account that does not exist, and the foreign key below would refuse it.
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "GroupInvitation",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "GroupInvitation",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParticipantUserId",
                table: "GroupInvitation",
                type: "uuid",
                nullable: true);

            // A stand-in per invitation, named after the address it was sent to. No identity
            // row, so nobody can sign in as it; no address, because there is no longer
            // anywhere for one to be read from.
            migrationBuilder.Sql(
                """
                INSERT INTO "User" ("Id", "FirstName")
                SELECT
                    invitation."Id",
                    COALESCE(NULLIF(left(split_part(invitation."Email", '@', 1), 64), ''), 'Invited')
                FROM "GroupInvitation" AS invitation
                WHERE NOT EXISTS (
                    SELECT 1 FROM "User" WHERE "User"."Id" = invitation."Id");
                """);

            // Two UUIDs' worth of randomness, hex, dashes out: 64 characters and 256 bits,
            // from the same strong generator gen_random_uuid() draws on. A token is the whole
            // of the authorisation on a position in a ledger, so it has to be unguessable
            // rather than merely unique -- md5(random()) would have been neither.
            migrationBuilder.Sql(
                """
                UPDATE "GroupInvitation" SET
                    "ParticipantUserId" = "Id",
                    "Name" = COALESCE(NULLIF(left(split_part("Email", '@', 1), 64), ''), 'Invited'),
                    "Token" = replace(gen_random_uuid()::text, '-', '') ||
                              replace(gen_random_uuid()::text, '-', '');
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "GroupInvitation"
                    ALTER COLUMN "Name" SET NOT NULL,
                    ALTER COLUMN "Token" SET NOT NULL,
                    ALTER COLUMN "ParticipantUserId" SET NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_GroupId_ParticipantUserId",
                table: "GroupInvitation",
                columns: new[] { "GroupId", "ParticipantUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_ParticipantUserId",
                table: "GroupInvitation",
                column: "ParticipantUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_Token",
                table: "GroupInvitation",
                column: "Token",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_GroupInvitation_User_ParticipantUserId",
                table: "GroupInvitation",
                column: "ParticipantUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Last, so everything above could read it.
            migrationBuilder.DropIndex(
                name: "IX_GroupInvitation_Email",
                table: "GroupInvitation");

            migrationBuilder.DropIndex(
                name: "IX_GroupInvitation_GroupId_Email",
                table: "GroupInvitation");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "GroupInvitation");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Reversible in shape and not in content. The addresses are gone, and the column
        /// that comes back is unique per group, so the rows cannot simply be left blank --
        /// two invitations in one group would collide on the empty string and the index
        /// would refuse to build. Each gets an obviously synthetic address at
        /// <c>invalid.example</c> instead: unique, restorable, and unmistakable for a real
        /// one, which is the honest signal that what was there is not coming back.
        /// <para>
        /// The stand-ins stay. They may be carrying shares by now, which is what they are
        /// for, and deleting them would take a group's spending history with them.
        /// </para>
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GroupInvitation_User_ParticipantUserId",
                table: "GroupInvitation");

            migrationBuilder.DropIndex(
                name: "IX_GroupInvitation_GroupId_ParticipantUserId",
                table: "GroupInvitation");

            migrationBuilder.DropIndex(
                name: "IX_GroupInvitation_ParticipantUserId",
                table: "GroupInvitation");

            migrationBuilder.DropIndex(
                name: "IX_GroupInvitation_Token",
                table: "GroupInvitation");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "GroupInvitation",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            // Before the columns it reads go, and before the unique index it has to satisfy
            // is built.
            migrationBuilder.Sql(
                """
                UPDATE "GroupInvitation"
                SET "Email" = left(lower("Name"), 40) || '+' || "Id"::text || '@invalid.example';
                """);

            migrationBuilder.DropColumn(
                name: "Name",
                table: "GroupInvitation");

            migrationBuilder.DropColumn(
                name: "ParticipantUserId",
                table: "GroupInvitation");

            migrationBuilder.DropColumn(
                name: "Token",
                table: "GroupInvitation");

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_Email",
                table: "GroupInvitation",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_GroupId_Email",
                table: "GroupInvitation",
                columns: new[] { "GroupId", "Email" },
                unique: true);
        }
    }
}
