using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class StaffOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Merchant",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "MerchantAlias",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantAlias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantAlias_Merchant_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MerchantRedirect",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantRedirect", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantRedirect_Merchant_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffActivity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChangesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffActivity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffActivity_User_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StaffMember",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMember", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffMember_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffSavedView",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Resource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffSavedView", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffSavedView_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupportNotification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportNotification", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupportNotification_SupportTicketMessage_MessageId",
                        column: x => x.MessageId,
                        principalTable: "SupportTicketMessage",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupportNotification_SupportTicket_TicketId",
                        column: x => x.TicketId,
                        principalTable: "SupportTicket",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupportNotification_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantAlias_MerchantId",
                table: "MerchantAlias",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantAlias_NormalizedName",
                table: "MerchantAlias",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantRedirect_MerchantId",
                table: "MerchantRedirect",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffActivity_ActorUserId",
                table: "StaffActivity",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffActivity_At",
                table: "StaffActivity",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_StaffActivity_Resource_ResourceId_At",
                table: "StaffActivity",
                columns: new[] { "Resource", "ResourceId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffMember_UserId",
                table: "StaffMember",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffSavedView_UserId_Resource_Name",
                table: "StaffSavedView",
                columns: new[] { "UserId", "Resource", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportNotification_MessageId",
                table: "SupportNotification",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportNotification_TicketId",
                table: "SupportNotification",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportNotification_UserId_ReadAt",
                table: "SupportNotification",
                columns: new[] { "UserId", "ReadAt" });
            // Reserve every existing canonical name in the same unique registry as aliases.
            migrationBuilder.Sql("""
                INSERT INTO "MerchantAlias" ("Id", "MerchantId", "Name", "NormalizedName", "CreatedAt")
                SELECT "Id", "Id", "Name", "NormalizedName", "FirstSeenAt" FROM "Merchant";

                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MerchantAlias");

            migrationBuilder.DropTable(
                name: "MerchantRedirect");

            migrationBuilder.DropTable(
                name: "StaffActivity");

            migrationBuilder.DropTable(
                name: "StaffMember");

            migrationBuilder.DropTable(
                name: "StaffSavedView");

            migrationBuilder.DropTable(
                name: "SupportNotification");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Merchant");
        }
    }
}
