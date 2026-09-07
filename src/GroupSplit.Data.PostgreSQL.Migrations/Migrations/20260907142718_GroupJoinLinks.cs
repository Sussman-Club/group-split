using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// The second way into a group: a link a member shares, rather than an address the app
    /// has to reach.
    /// </summary>
    /// <remarks>
    /// One table and nothing else. No column is added to anything that exists, so there is
    /// no data step and no backfill -- a group without a link is a group nobody has made one
    /// for, which is every group there is on the day this runs.
    /// <para>
    /// <c>Token</c> is unique because it is the whole of the lookup: somebody opens a URL
    /// and the token in it is all the request carries. <c>RevokedAt</c> is nullable, and
    /// null is not a missing value -- it is what "still stands" looks like. The row is kept
    /// after revocation so that opening a withdrawn link can say it was withdrawn instead of
    /// that it never existed.
    /// </para>
    /// <para>
    /// <c>Down</c> drops the table, which is every trace of it.
    /// </para>
    /// </remarks>
    public partial class GroupJoinLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupJoinLink",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupJoinLink", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupJoinLink_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupJoinLink_User_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupJoinLink_CreatedByUserId",
                table: "GroupJoinLink",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupJoinLink_GroupId_CreatedAt",
                table: "GroupJoinLink",
                columns: new[] { "GroupId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupJoinLink_Token",
                table: "GroupJoinLink",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupJoinLink");
        }
    }
}
