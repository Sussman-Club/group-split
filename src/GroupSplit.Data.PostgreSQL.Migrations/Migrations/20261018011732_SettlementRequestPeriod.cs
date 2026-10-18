using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class SettlementRequestPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "Through",
                table: "SettlementRequest",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UtcOffset",
                table: "SettlementRequest",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Through",
                table: "SettlementRequest");

            migrationBuilder.DropColumn(
                name: "UtcOffset",
                table: "SettlementRequest");
        }
    }
}
