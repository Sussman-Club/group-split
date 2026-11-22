using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RecurringExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecurringExpense",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnteredById = table.Column<Guid>(type: "uuid", nullable: false),
                    Template = table.Column<string>(type: "jsonb", nullable: false),
                    Frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    NextOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PausedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Problem = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringExpense", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringExpense_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurringExpense_User_EnteredById",
                        column: x => x.EnteredById,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpense_EnteredById",
                table: "RecurringExpense",
                column: "EnteredById");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpense_GroupId",
                table: "RecurringExpense",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpense_StoppedAt_PausedAt_NextOn",
                table: "RecurringExpense",
                columns: new[] { "StoppedAt", "PausedAt", "NextOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringExpense");
        }
    }
}
