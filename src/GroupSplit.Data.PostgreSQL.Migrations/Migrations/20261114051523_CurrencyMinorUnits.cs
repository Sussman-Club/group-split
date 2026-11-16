using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class CurrencyMinorUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Attached receipts previously defaulted to USD even when their expense had
            // another currency. Their figures are in the expense's original currency.
            migrationBuilder.Sql("""
                UPDATE "Receipt" AS r SET "Currency" = t."Currency"
                FROM "Transaction" AS t WHERE r."ExpenseId" = t."Id";
                """);
            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "TransactionSplit",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "Transaction",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "RequestedAmount",
                table: "SettlementRequestGroup",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "DebtAtCreation",
                table: "SettlementRequestGroup",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "SettlementRequest",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitPrice",
                table: "ReceiptItem",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalPrice",
                table: "ReceiptItem",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "TaxAmount",
                table: "ReceiptItem",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2,
                oldDefaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "Total",
                table: "Receipt",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "Tip",
                table: "Receipt",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "Tax",
                table: "Receipt",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "Subtotal",
                table: "Receipt",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "BankTransaction",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Narrowing numeric scale would silently round recorded amounts. Refuse the
            // downgrade until the operator has dealt with any three-decimal records.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "Transaction" WHERE "Amount" <> round("Amount", 2))
                       OR EXISTS (SELECT 1 FROM "TransactionSplit" WHERE "Amount" <> round("Amount", 2))
                       OR EXISTS (SELECT 1 FROM "BankTransaction" WHERE "Amount" <> round("Amount", 2))
                       OR EXISTS (SELECT 1 FROM "SettlementRequest" WHERE "Amount" <> round("Amount", 2))
                       OR EXISTS (SELECT 1 FROM "SettlementRequestGroup" WHERE "DebtAtCreation" <> round("DebtAtCreation", 2) OR "RequestedAmount" <> round("RequestedAmount", 2))
                       OR EXISTS (SELECT 1 FROM "Receipt" WHERE "Subtotal" <> round("Subtotal", 2) OR "Tax" <> round("Tax", 2) OR "Tip" <> round("Tip", 2) OR "Total" <> round("Total", 2))
                       OR EXISTS (SELECT 1 FROM "ReceiptItem" WHERE "UnitPrice" <> round("UnitPrice", 2) OR "TotalPrice" <> round("TotalPrice", 2) OR "TaxAmount" <> round("TaxAmount", 2)) THEN
                        RAISE EXCEPTION 'Cannot downgrade currency precision while three-decimal amounts exist';
                    END IF;
                END $$;
                """);
            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "TransactionSplit",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "Transaction",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "RequestedAmount",
                table: "SettlementRequestGroup",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "DebtAtCreation",
                table: "SettlementRequestGroup",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "SettlementRequest",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitPrice",
                table: "ReceiptItem",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalPrice",
                table: "ReceiptItem",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "TaxAmount",
                table: "ReceiptItem",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3,
                oldDefaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "Total",
                table: "Receipt",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "Tip",
                table: "Receipt",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "Tax",
                table: "Receipt",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "Subtotal",
                table: "Receipt",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "BankTransaction",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);
        }
    }
}
