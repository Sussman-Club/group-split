using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSharesRuleVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharesRuleVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharesRuleVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharesRuleVersion_RuleVersion_Id",
                        column: x => x.Id,
                        principalTable: "RuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharesRuleUser",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Shares = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharesRuleUser", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharesRuleUser_SharesRuleVersion_RuleVersionId",
                        column: x => x.RuleVersionId,
                        principalTable: "SharesRuleVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SharesRuleUser_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharesRuleUser_RuleVersionId",
                table: "SharesRuleUser",
                column: "RuleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SharesRuleUser_UserId_RuleVersionId",
                table: "SharesRuleUser",
                columns: new[] { "UserId", "RuleVersionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharesRuleUser");

            migrationBuilder.DropTable(
                name: "SharesRuleVersion");
        }
    }
}
