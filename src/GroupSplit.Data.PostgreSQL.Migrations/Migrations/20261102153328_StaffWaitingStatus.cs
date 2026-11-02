using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class StaffWaitingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
