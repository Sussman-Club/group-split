using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupMembershipDeparture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeftAt",
                table: "GroupUser",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeftByUserId",
                table: "GroupUser",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeftReason",
                table: "GroupUser",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupUser_LeftByUserId",
                table: "GroupUser",
                column: "LeftByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_GroupUser_User_LeftByUserId",
                table: "GroupUser",
                column: "LeftByUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without the column a closed row reads as a current membership, so dropping it
            // as it stands would put everybody who has left back into their groups. Before
            // this migration a departure deleted the row, which is what this goes back to.
            migrationBuilder.Sql("""DELETE FROM "GroupUser" WHERE "LeftAt" IS NOT NULL;""");

            migrationBuilder.DropForeignKey(
                name: "FK_GroupUser_User_LeftByUserId",
                table: "GroupUser");

            migrationBuilder.DropIndex(
                name: "IX_GroupUser_LeftByUserId",
                table: "GroupUser");

            migrationBuilder.DropColumn(
                name: "LeftAt",
                table: "GroupUser");

            migrationBuilder.DropColumn(
                name: "LeftByUserId",
                table: "GroupUser");

            migrationBuilder.DropColumn(
                name: "LeftReason",
                table: "GroupUser");
        }
    }
}
