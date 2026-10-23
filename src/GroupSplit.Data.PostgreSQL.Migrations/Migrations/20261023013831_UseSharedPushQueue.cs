using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class UseSharedPushQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve pending deliveries when upgrading an installation of the earlier PR.
            // Stop the old API workers while applying this migration.
            migrationBuilder.Sql("""
                INSERT INTO "BackgroundJob"
                    ("Id", "Type", "Version", "Payload", "Status", "Attempts", "CreatedAt", "AvailableAt")
                SELECT "Id", 'push.send-expense', 1,
                    jsonb_build_object('DeviceId', "DeviceId", 'UserId', "UserId", 'GroupId', "GroupId",
                        'DeliveryId', "Id", 'ExpiresAt', "ExpiresAt"),
                    'Pending', "Attempts", CURRENT_TIMESTAMP, "NextAttemptAt"
                FROM "PushDelivery"
                WHERE "ExpiresAt" > CURRENT_TIMESTAMP AND "Attempts" < 5;
                """);
            migrationBuilder.DropTable(
                name: "PushDelivery");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PushDelivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDelivery", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushDelivery_Group_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushDelivery_PushDevice_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "PushDevice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushDelivery_DeviceId",
                table: "PushDelivery",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_PushDelivery_GroupId",
                table: "PushDelivery",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PushDelivery_NextAttemptAt",
                table: "PushDelivery",
                column: "NextAttemptAt");
        }
    }
}
