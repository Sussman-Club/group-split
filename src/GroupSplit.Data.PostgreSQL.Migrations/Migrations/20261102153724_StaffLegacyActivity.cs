using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class StaffLegacyActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "StaffActivity" ("Id", "ActorUserId", "Resource", "ResourceId", "Action", "At", "ChangesJson")
                SELECT h."Id", h."ChangedByUserId", 'Tickets', h."TicketId", 'Status changed', h."ChangedAt",
                    json_build_array(json_build_object('Field', 'Status', 'Before', h."FromStatus", 'After', h."ToStatus"))::text
                FROM "SupportTicketStatusChange" h
                WHERE NOT EXISTS (SELECT 1 FROM "StaffActivity" a
                    WHERE a."Resource" = 'Tickets' AND a."ResourceId" = h."TicketId" AND a."At" = h."ChangedAt")
                ON CONFLICT ("Id") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
