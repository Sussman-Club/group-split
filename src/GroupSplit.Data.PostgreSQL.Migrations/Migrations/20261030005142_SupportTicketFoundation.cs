using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class SupportTicketFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupportTicket",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    ReproductionSteps = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DiagnosticsConsent = table.Column<bool>(type: "boolean", nullable: false),
                    Platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OperatingSystemVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BrowserVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DuplicateOfTicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    GitHubRepository = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GitHubIssueNumber = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTicket", x => x.Id);
                    table.CheckConstraint("CK_SupportTicket_DiagnosticsConsent", "\"DiagnosticsConsent\" OR (\"Platform\" IS NULL AND \"OperatingSystemVersion\" IS NULL AND \"BrowserVersion\" IS NULL)");
                    table.CheckConstraint("CK_SupportTicket_Duplicate", "\"DuplicateOfTicketId\" IS NULL OR \"DuplicateOfTicketId\" <> \"Id\"");
                    table.CheckConstraint("CK_SupportTicket_GitHub", "(\"GitHubRepository\" IS NULL AND \"GitHubIssueNumber\" IS NULL) OR (\"GitHubRepository\" IS NOT NULL AND length(trim(\"GitHubRepository\")) > 0 AND \"GitHubIssueNumber\" IS NOT NULL AND \"GitHubIssueNumber\" > 0)");
                    table.CheckConstraint("CK_SupportTicket_Priority", "\"Priority\" IN ('Low', 'Normal', 'High', 'Urgent')");
                    table.CheckConstraint("CK_SupportTicket_Status", "\"Status\" IN ('New', 'UnderReview', 'InProgress', 'Resolved', 'Closed')");
                    table.CheckConstraint("CK_SupportTicket_Type", "\"Type\" IN ('Bug', 'FeatureRequest', 'Support')");
                    table.ForeignKey(
                        name: "FK_SupportTicket_SupportTicket_DuplicateOfTicketId",
                        column: x => x.DuplicateOfTicketId,
                        principalTable: "SupportTicket",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupportTicket_User_AssignedUserId",
                        column: x => x.AssignedUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SupportTicket_User_ReporterUserId",
                        column: x => x.ReporterUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupportTicketAttachment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTicketAttachment", x => x.Id);
                    table.CheckConstraint("CK_SupportTicketAttachment_Length", "\"Length\" > 0");
                    table.ForeignKey(
                        name: "FK_SupportTicketAttachment_SupportTicket_TicketId",
                        column: x => x.TicketId,
                        principalTable: "SupportTicket",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupportTicketAttachment_User_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SupportTicketMessage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTicketMessage", x => x.Id);
                    table.CheckConstraint("CK_SupportTicketMessage_Visibility", "\"Visibility\" IN ('Public', 'Internal')");
                    table.ForeignKey(
                        name: "FK_SupportTicketMessage_SupportTicket_TicketId",
                        column: x => x.TicketId,
                        principalTable: "SupportTicket",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupportTicketMessage_User_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SupportTicketStatusChange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FromStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTicketStatusChange", x => x.Id);
                    table.CheckConstraint("CK_SupportTicketStatusChange_From", "\"FromStatus\" IS NULL OR \"FromStatus\" IN ('New', 'UnderReview', 'InProgress', 'Resolved', 'Closed')");
                    table.CheckConstraint("CK_SupportTicketStatusChange_To", "\"ToStatus\" IN ('New', 'UnderReview', 'InProgress', 'Resolved', 'Closed')");
                    table.ForeignKey(
                        name: "FK_SupportTicketStatusChange_SupportTicket_TicketId",
                        column: x => x.TicketId,
                        principalTable: "SupportTicket",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupportTicketStatusChange_User_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicket_AssignedUserId_Status",
                table: "SupportTicket",
                columns: new[] { "AssignedUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicket_DuplicateOfTicketId",
                table: "SupportTicket",
                column: "DuplicateOfTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicket_GitHubRepository_GitHubIssueNumber",
                table: "SupportTicket",
                columns: new[] { "GitHubRepository", "GitHubIssueNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicket_ReporterUserId_CreatedAt",
                table: "SupportTicket",
                columns: new[] { "ReporterUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicket_Status_Priority_CreatedAt",
                table: "SupportTicket",
                columns: new[] { "Status", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketAttachment_ObjectKey",
                table: "SupportTicketAttachment",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketAttachment_TicketId_UploadedAt",
                table: "SupportTicketAttachment",
                columns: new[] { "TicketId", "UploadedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketAttachment_UploadedByUserId",
                table: "SupportTicketAttachment",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketMessage_AuthorUserId",
                table: "SupportTicketMessage",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketMessage_TicketId_CreatedAt",
                table: "SupportTicketMessage",
                columns: new[] { "TicketId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketStatusChange_ChangedByUserId",
                table: "SupportTicketStatusChange",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketStatusChange_TicketId_ChangedAt",
                table: "SupportTicketStatusChange",
                columns: new[] { "TicketId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupportTicketAttachment");

            migrationBuilder.DropTable(
                name: "SupportTicketMessage");

            migrationBuilder.DropTable(
                name: "SupportTicketStatusChange");

            migrationBuilder.DropTable(
                name: "SupportTicket");
        }
    }
}
