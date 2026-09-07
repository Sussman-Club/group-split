using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Gives imported bank data a side of its own, and the ledger one nullable column that
    /// points at it.
    /// </summary>
    /// <remarks>
    /// <c>BankConnection</c>, <c>LinkedAccount</c> and <c>BankTransaction</c> are the
    /// staging side of the seam the Phase 3 plan describes: rows arrive here from a
    /// provider and become transactions only when a person files them.
    /// <c>Transaction.BankTransactionId</c> is set by filing and by nothing else, which is
    /// why it is nullable and stays so -- most transactions are typed in, and null is the
    /// honest answer for those.
    /// <para>
    /// <c>DataProtectionKeys</c> is the framework's key ring, kept in the app database so
    /// that the ciphertext of a bank access token and the key that opens it live and die
    /// together.
    /// </para>
    /// <para>
    /// Purely additive. Nothing existing has a row to backfill, so there is no data step,
    /// and <c>Down</c> drops what <c>Up</c> made.
    /// </para>
    /// </remarks>
    public partial class BankConnectionsAndImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BankTransactionId",
                table: "Transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankConnection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderItemId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    InstitutionName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccessTokenCiphertext = table.Column<string>(type: "text", nullable: false),
                    Cursor = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankConnection_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LinkedAccount",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderAccountId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Mask = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Subtype = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false, defaultValue: "USD")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedAccount", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkedAccount_BankConnection_BankConnectionId",
                        column: x => x.BankConnectionId,
                        principalTable: "BankConnection",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BankTransaction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false, defaultValue: "USD"),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MerchantName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProviderCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Pending = table.Column<bool>(type: "boolean", nullable: false),
                    ReplacesId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankTransaction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankTransaction_BankTransaction_ReplacesId",
                        column: x => x.ReplacesId,
                        principalTable: "BankTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BankTransaction_LinkedAccount_LinkedAccountId",
                        column: x => x.LinkedAccountId,
                        principalTable: "LinkedAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_BankTransactionId",
                table: "Transaction",
                column: "BankTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnection_Provider_ProviderItemId",
                table: "BankConnection",
                columns: new[] { "Provider", "ProviderItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnection_UserId",
                table: "BankConnection",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransaction_LinkedAccountId_ProviderTransactionId",
                table: "BankTransaction",
                columns: new[] { "LinkedAccountId", "ProviderTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankTransaction_LinkedAccountId_Status_Date",
                table: "BankTransaction",
                columns: new[] { "LinkedAccountId", "Status", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransaction_ReplacesId",
                table: "BankTransaction",
                column: "ReplacesId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkedAccount_BankConnectionId_ProviderAccountId",
                table: "LinkedAccount",
                columns: new[] { "BankConnectionId", "ProviderAccountId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Transaction_BankTransaction_BankTransactionId",
                table: "Transaction",
                column: "BankTransactionId",
                principalTable: "BankTransaction",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transaction_BankTransaction_BankTransactionId",
                table: "Transaction");

            migrationBuilder.DropTable(
                name: "BankTransaction");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "LinkedAccount");

            migrationBuilder.DropTable(
                name: "BankConnection");

            migrationBuilder.DropIndex(
                name: "IX_Transaction_BankTransactionId",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "BankTransactionId",
                table: "Transaction");
        }
    }
}
