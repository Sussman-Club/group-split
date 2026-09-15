using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    public partial class CompletePendingBankReceiptAttachments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ExpenseId",
                table: "ReceiptAttachment",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "BankTransactionId",
                table: "ReceiptAttachment",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptAttachment_BankTransactionId",
                table: "ReceiptAttachment",
                column: "BankTransactionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptAttachment_BankTransaction_BankTransactionId",
                table: "ReceiptAttachment",
                column: "BankTransactionId",
                principalTable: "BankTransaction",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReceiptAttachment_BankTransaction_BankTransactionId",
                table: "ReceiptAttachment");

            migrationBuilder.DropIndex(
                name: "IX_ReceiptAttachment_BankTransactionId",
                table: "ReceiptAttachment");

            migrationBuilder.DropColumn(
                name: "BankTransactionId",
                table: "ReceiptAttachment");

            migrationBuilder.AlterColumn<Guid>(
                name: "ExpenseId",
                table: "ReceiptAttachment",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
