using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// A settlement can say what period it was for: the last day of activity it settles.
    /// </summary>
    /// <remarks>
    /// Nullable and not backfilled, deliberately. Every existing settlement was an amount
    /// somebody typed or the whole of a balance at the time, and neither claims a period;
    /// giving each its own date would have every partial payment announce "covers through"
    /// a day it never settled.
    /// </remarks>
    public partial class AddTransferCoversThrough : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "CoversThrough",
                table: "Transaction",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoversThrough",
                table: "Transaction");
        }
    }
}
