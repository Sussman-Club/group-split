using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PendingBankLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingBankLink",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicTokenCiphertext = table.Column<string>(type: "text", nullable: true),
                    ItemCiphertext = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingBankLink", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingBankLink_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingBankLink_Attempts_StartedAt",
                table: "PendingBankLink",
                columns: new[] { "Attempts", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingBankLink_UserId",
                table: "PendingBankLink",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingBankLink");
        }
    }
}
