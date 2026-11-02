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
            migrationBuilder.AddColumn<bool>(name: "StaffSupportEnabled", table: "PushDevice",
                type: "boolean", nullable: false, defaultValue: false);
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


            migrationBuilder.DropCheckConstraint(
                name: "CK_SupportTicketStatusChange_From",
                table: "SupportTicketStatusChange");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupportTicketStatusChange_To",
                table: "SupportTicketStatusChange");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupportTicket_Status",
                table: "SupportTicket");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupportTicketStatusChange_From",
                table: "SupportTicketStatusChange",
                sql: "\"FromStatus\" IS NULL OR \"FromStatus\" IN ('New', 'UnderReview', 'InProgress', 'WaitingForCustomer', 'Resolved', 'Closed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupportTicketStatusChange_To",
                table: "SupportTicketStatusChange",
                sql: "\"ToStatus\" IN ('New', 'UnderReview', 'InProgress', 'WaitingForCustomer', 'Resolved', 'Closed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupportTicket_Status",
                table: "SupportTicket",
                sql: "\"Status\" IN ('New', 'UnderReview', 'InProgress', 'WaitingForCustomer', 'Resolved', 'Closed')");


            migrationBuilder.Sql("""
                INSERT INTO "StaffActivity" ("Id", "ActorUserId", "Resource", "ResourceId", "Action", "At", "ChangesJson")
                SELECT h."Id", h."ChangedByUserId", 'Tickets', h."TicketId", 'Status changed', h."ChangedAt",
                    json_build_array(json_build_object('Field', 'Status', 'Before', h."FromStatus", 'After', h."ToStatus"))::text
                FROM "SupportTicketStatusChange" h
                WHERE NOT EXISTS (SELECT 1 FROM "StaffActivity" a
                    WHERE a."Resource" = 'Tickets' AND a."ResourceId" = h."TicketId" AND a."At" = h."ChangedAt")
                ON CONFLICT ("Id") DO NOTHING;
                """);


        migrationBuilder.Sql("""
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE INDEX "IX_SupportTicket_UpdatedAt_Id" ON "SupportTicket" ("UpdatedAt" DESC, "Id" DESC);
            CREATE INDEX "IX_SupportTicket_Status_UpdatedAt_Id" ON "SupportTicket" ("Status", "UpdatedAt" DESC, "Id" DESC);
            CREATE INDEX "IX_SupportTicket_Assigned_UpdatedAt_Id" ON "SupportTicket" ("AssignedUserId", "UpdatedAt" DESC, "Id" DESC);
            CREATE INDEX "IX_SupportTicket_Title_Trgm" ON "SupportTicket" USING gin (lower("Title") gin_trgm_ops);
            CREATE INDEX "IX_SupportTicket_Description_Trgm" ON "SupportTicket" USING gin (lower("Description") gin_trgm_ops);
            CREATE INDEX "IX_Merchant_Name_Trgm" ON "Merchant" USING gin ("NormalizedName" gin_trgm_ops);
            CREATE INDEX "IX_MerchantAlias_Name_Trgm" ON "MerchantAlias" USING gin ("NormalizedName" gin_trgm_ops);
            CREATE INDEX "IX_User_Email_Trgm" ON "User" USING gin (lower("Email") gin_trgm_ops);
            CREATE INDEX "IX_User_FullName_Trgm" ON "User" USING gin (lower(coalesce("FirstName", '') || ' ' || coalesce("LastName", '')) gin_trgm_ops);
            """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        migrationBuilder.Sql("""
            DROP INDEX "IX_User_FullName_Trgm";
            DROP INDEX "IX_User_Email_Trgm";
            DROP INDEX "IX_MerchantAlias_Name_Trgm";
            DROP INDEX "IX_Merchant_Name_Trgm";
            DROP INDEX "IX_SupportTicket_Description_Trgm";
            DROP INDEX "IX_SupportTicket_Title_Trgm";
            DROP INDEX "IX_SupportTicket_Assigned_UpdatedAt_Id";
            DROP INDEX "IX_SupportTicket_Status_UpdatedAt_Id";
            DROP INDEX "IX_SupportTicket_UpdatedAt_Id";
            """);


            migrationBuilder.DropCheckConstraint(
                name: "CK_SupportTicketStatusChange_From",
                table: "SupportTicketStatusChange");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupportTicketStatusChange_To",
                table: "SupportTicketStatusChange");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupportTicket_Status",
                table: "SupportTicket");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupportTicketStatusChange_From",
                table: "SupportTicketStatusChange",
                sql: "\"FromStatus\" IS NULL OR \"FromStatus\" IN ('New', 'UnderReview', 'InProgress', 'Resolved', 'Closed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupportTicketStatusChange_To",
                table: "SupportTicketStatusChange",
                sql: "\"ToStatus\" IN ('New', 'UnderReview', 'InProgress', 'Resolved', 'Closed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupportTicket_Status",
                table: "SupportTicket",
                sql: "\"Status\" IN ('New', 'UnderReview', 'InProgress', 'Resolved', 'Closed')");


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

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Merchant");

            migrationBuilder.DropColumn(name: "StaffSupportEnabled", table: "PushDevice");

        }
    }
}
