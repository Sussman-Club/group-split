using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSplitRulesCategoriesAndTransactionSplits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Group",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.CreateTable(
                name: "SplitRule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SplitRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SplitRule_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TransactionSplit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionSplit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionSplit_Transaction_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TransactionSplit_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Category",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefaultSplitRuleId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Category", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Category_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Category_SplitRule_DefaultSplitRuleId",
                        column: x => x.DefaultSplitRuleId,
                        principalTable: "SplitRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SplitRuleParticipant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SplitRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Weight = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SplitRuleParticipant", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SplitRuleParticipant_SplitRule_SplitRuleId",
                        column: x => x.SplitRuleId,
                        principalTable: "SplitRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SplitRuleParticipant_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Category_DefaultSplitRuleId",
                table: "Category",
                column: "DefaultSplitRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Category_GroupId_Name",
                table: "Category",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SplitRule_GroupId_Name",
                table: "SplitRule",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SplitRuleParticipant_SplitRuleId_UserId",
                table: "SplitRuleParticipant",
                columns: new[] { "SplitRuleId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SplitRuleParticipant_UserId",
                table: "SplitRuleParticipant",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionSplit_TransactionId_UserId",
                table: "TransactionSplit",
                columns: new[] { "TransactionId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionSplit_UserId",
                table: "TransactionSplit",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Category");

            migrationBuilder.DropTable(
                name: "SplitRuleParticipant");

            migrationBuilder.DropTable(
                name: "TransactionSplit");

            migrationBuilder.DropTable(
                name: "SplitRule");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Group");
        }
    }
}
