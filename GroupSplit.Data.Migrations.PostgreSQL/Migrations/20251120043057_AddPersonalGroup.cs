using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_User_Group_PersonalGroupId",
                table: "User");

            migrationBuilder.DropIndex(
                name: "IX_User_PersonalGroupId",
                table: "User");

            migrationBuilder.DropColumn(
                name: "PersonalGroupId",
                table: "User");
        }
    }
}
