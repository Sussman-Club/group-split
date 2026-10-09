using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <summary>
    /// The Keycloak logins still owed a deletion, by accounts that have already been deleted.
    /// </summary>
    /// <remarks>
    /// A new table and nothing to backfill. Accounts deleted before this kept their login in
    /// Keycloak and had their subject removed here, so there is no row left that could say which
    /// login was theirs -- and some of those logins have signed in since and own a fresh account.
    /// Finding the ones nobody uses is an operator's job at the admin console, not this one's.
    /// </remarks>
    public partial class AddPendingLoginDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingLoginDeletion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingLoginDeletion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingLoginDeletion_IdentityId",
                table: "PendingLoginDeletion",
                column: "IdentityId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingLoginDeletion");
        }
    }
}
