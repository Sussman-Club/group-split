using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class SettlementRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SettlementRunId",
                table: "Transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WrittenByRunId",
                table: "Transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SettlementRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RanByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RanAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EffectiveDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReopenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettlementRun_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SettlementRun_User_RanByUserId",
                        column: x => x.RanByUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_GroupId_SettlementRunId",
                table: "Transaction",
                columns: new[] { "GroupId", "SettlementRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_SettlementRunId",
                table: "Transaction",
                column: "SettlementRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_WrittenByRunId",
                table: "Transaction",
                column: "WrittenByRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementRun_GroupId_EffectiveDate",
                table: "SettlementRun",
                columns: new[] { "GroupId", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SettlementRun_RanByUserId",
                table: "SettlementRun",
                column: "RanByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_SettlementRun_SettlementRunId",
                table: "Transaction",
                column: "SettlementRunId",
                principalTable: "SettlementRun",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_SettlementRun_WrittenByRunId",
                table: "Transaction",
                column: "WrittenByRunId",
                principalTable: "SettlementRun",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_SettlementRun_SettlementRunId",
                table: "Transaction");

            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_SettlementRun_WrittenByRunId",
                table: "Transaction");

            migrationBuilder.DropTable(
                name: "SettlementRun");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_GroupId_SettlementRunId",
                table: "Transaction");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_SettlementRunId",
                table: "Transaction");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_WrittenByRunId",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "SettlementRunId",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "WrittenByRunId",
                table: "Transaction");
        }
    }
}
