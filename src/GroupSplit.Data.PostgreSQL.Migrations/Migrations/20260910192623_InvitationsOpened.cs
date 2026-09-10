using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// Remembers that an account has opened an invitation's link, so the invitation can be
    /// put back in front of them.
    /// </summary>
    /// <remarks>
    /// The invitee's own list, rebuilt. It used to be a query -- every invitation whose
    /// address matches your profile -- and there are no addresses on an invitation any
    /// more, so there is nothing to query and this has to be written down as it happens.
    /// <para>
    /// A new table with nothing to backfill: the invitations that exist have not been
    /// opened by anybody yet, because until now nothing was watching.
    /// </para>
    /// </remarks>
    public partial class InvitationsOpened : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvitationOpened",
                columns: table => new
                {
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvitationOpened", x => new { x.InvitationId, x.UserId });
                    table.ForeignKey(
                        name: "FK_InvitationOpened_GroupInvitation_InvitationId",
                        column: x => x.InvitationId,
                        principalTable: "GroupInvitation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvitationOpened_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvitationOpened_UserId",
                table: "InvitationOpened",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvitationOpened");
        }
    }
}
