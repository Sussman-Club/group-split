using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class ChangeRuleVersionInheritanceToTPT : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PercentRuleUser_RuleVersion_RuleVersionId",
                table: "PercentRuleUser");

            migrationBuilder.DropForeignKey(
                name: "FK_RuleVersion_User_OtherUserId",
                table: "RuleVersion");

            migrationBuilder.DropIndex(
                name: "IX_RuleVersion_OtherUserId",
                table: "RuleVersion");

            migrationBuilder.DropColumn(
                name: "OtherUserId",
                table: "RuleVersion");

            migrationBuilder.DropColumn(
                name: "RuleType",
                table: "RuleVersion");

            migrationBuilder.CreateTable(
                name: "PercentRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PercentRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PercentRuleVersion_RuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "RuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonalRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonalRuleVersion_RuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "RuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SettlementRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OtherUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettlementRuleVersion_RuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "RuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SettlementRuleVersion_User_OtherUserId",
                        column: x => x.OtherUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SettlementRuleVersion_OtherUserId",
                table: "SettlementRuleVersion",
                column: "OtherUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_PercentRuleUser_PercentRuleVersion_RuleVersionId",
                table: "PercentRuleUser",
                column: "RuleVersionId",
                principalTable: "PercentRuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PercentRuleUser_PercentRuleVersion_RuleVersionId",
                table: "PercentRuleUser");

            migrationBuilder.DropTable(
                name: "PercentRuleVersion");

            migrationBuilder.DropTable(
                name: "PersonalRuleVersion");

            migrationBuilder.DropTable(
                name: "SettlementRuleVersion");

            migrationBuilder.AddColumn<Guid>(
                name: "OtherUserId",
                table: "RuleVersion",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleType",
                table: "RuleVersion",
                type: "character varying(13)",
                maxLength: 13,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_RuleVersion_OtherUserId",
                table: "RuleVersion",
                column: "OtherUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_PercentRuleUser_RuleVersion_RuleVersionId",
                table: "PercentRuleUser",
                column: "RuleVersionId",
                principalTable: "RuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RuleVersion_User_OtherUserId",
                table: "RuleVersion",
                column: "OtherUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
