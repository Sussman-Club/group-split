using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// The itemised bill behind an expense, and dividing by who had what.
/// </summary>
/// <remarks>
/// For the dinner where nobody ate the same thing. An even split is wrong for it, a
/// percentage rule is a guess at it, and stating five amounts by hand means adding the
/// column up yourself and then apportioning the tax and the tip -- which is the part people
/// get wrong. Typing the lines and saying who had them is the same information, and the
/// arithmetic falls out.
/// <para>
/// Transcribing a bill and dividing by it are separate commands on purpose: the paper is
/// typed in one sitting, and who had what is settled over the rest of the evening. Nothing
/// touches the ledger until <c>divide</c>.
/// </para>
/// <para>
/// Tax is carried by the line it was charged on, so one bill can charge two rates. The tip
/// belongs to no line and is spread over them by price, which is the only division of it
/// that does not depend on who ordered the expensive thing.
/// </para>
/// </remarks>
public static class ReceiptCommands
{
    private static readonly Argument<Guid> TransactionId = new("transaction-id")
    {
        Description = "The expense's id, as shown by `groupsplit transactions list`."
    };

    public static Command Build()
    {
        var receipts = new Command("receipts",
            "Itemised bills: what was on them, who had what, and dividing by it.");

        receipts.Subcommands.Add(Show());
        receipts.Subcommands.Add(Set());
        receipts.Subcommands.Add(Rule());
        receipts.Subcommands.Add(Preview());
        receipts.Subcommands.Add(Divide());
        receipts.Subcommands.Add(Delete());

        return receipts;
    }

    private static Command Show()
    {
        var command = new Command("show", "Show a bill, its lines, and the rule each line divides by.")
        {
            TransactionId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(TransactionId);
            var client = new Api.ReceiptsClient(context.ApiHttpClient);

            var receipt = await client.GetReceiptAsync(id, ct);

            context.Output.Write(receipt, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Set()
    {
        var items = new Option<string[]>("--item")
        {
            Description = "A line on the bill, as <name>=<price>[x<qty>][/tax<amount>][@<rule-version-id>], "
                          + "repeatable. /notax says the bill's tax was not charged on it. "
                          + "Rules are optional while editing and can be set later with "
                          + "`groupsplit receipts rule`.",
            AllowMultipleArgumentsPerToken = false
        };

        var subtotal = new Option<decimal?>("--subtotal")
        {
            Description = "What the lines came to before tax and tip. "
                          + "Defaults to the lines added up."
        };

        var tax = new Option<decimal>("--tax") { Description = "Tax on the whole bill." };
        var tip = new Option<decimal>("--tip") { Description = "Tip on the whole bill." };

        var total = new Option<decimal?>("--total")
        {
            Description = "What was paid. Defaults to subtotal plus tax plus tip, and must "
                          + "equal the expense's amount."
        };

        var command = new Command("set",
            "Transcribe a bill, replacing whatever was there. Does not divide anything.")
        {
            TransactionId, items, subtotal, tax, tip, total
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(TransactionId);

            if (parse.GetValue(items) is not { Length: > 0 } given)
            {
                throw CliException.Input(
                    "A receipt needs at least one --item.",
                    "Try --item \"Wine=18.00\", repeating --item for each line on the bill.");
            }

            var lines = ReceiptItems.Parse("--item", given);

            // Both default rather than being required, because on an ordinary bill they are
            // the lines added up and then the tax and tip added on -- and making somebody
            // retype a figure the machine can add is how a transcription error gets in.
            // Stating them is for the bill that disagrees with its own arithmetic, which is
            // exactly when the server should refuse it.
            var lineTotal = lines.Sum(line => line.TotalPrice);
            var subtotalValue = parse.GetValue(subtotal) ?? lineTotal;

            var request = new SaveReceiptRequest
            {
                Subtotal = subtotalValue,
                Tax = parse.GetValue(tax),
                Tip = parse.GetValue(tip),
                Total = parse.GetValue(total)
                        ?? subtotalValue + parse.GetValue(tax) + parse.GetValue(tip),
                Items = lines
            };

            var client = new Api.ReceiptsClient(context.ApiHttpClient);

            var receipt = await client.SaveReceiptAsync(id, request, ct);

            context.Output.Write(receipt, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Rule()
    {
        var itemId = new Argument<Guid>("item-id");
        var version = new Option<Guid?>("--rule-version") { Description = "Saved rule version to assign. Omit to clear the item rule." };
        var command = new Command("rule", "Choose the split rule version for an item.") { TransactionId, itemId, version };
        command.SetHandler(async (context, ct) =>
        {
            var receipt = await new Api.ReceiptsClient(context.ApiHttpClient).SetReceiptItemRuleAsync(
                context.ParseResult.GetValue(TransactionId), context.ParseResult.GetValue(itemId),
                new SetReceiptItemRuleRequest { SplitRuleVersionId = context.ParseResult.GetValue(version) }, ct);
            context.Output.Write(receipt, Render);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command Preview()
    {
        var command = new Command("preview",
            "What dividing by the bill would come to. Changes nothing.") { TransactionId };

        command.SetHandler(async (context, ct) =>
        {
            var division = await new Api.ReceiptsClient(context.ApiHttpClient)
                .PreviewReceiptDivisionAsync(context.ParseResult.GetValue(TransactionId), ct);

            context.Output.Write(division, RenderDivision);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Divide()
    {
        var command = new Command("divide",
            "Divide the expense by its bill and store the shares.") { TransactionId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(TransactionId);
            var client = new Api.ReceiptsClient(context.ApiHttpClient);

            // The preview first, so the confirmation says what the shares become rather than
            // that they will change. This is money moving between people's balances, and
            // "Alice 24.50, Omar 15.50" is the thing worth reading before it happens.
            var division = await client.PreviewReceiptDivisionAsync(id, ct);

            Confirmation.Require(
                context,
                action: "receipts.divide",
                summary: $"Divide this expense of {division.Total} by its bill?",
                changes:
                [
                    .. division.Shares.Select(share =>
                        $"{share.UserId} owes {share.Amount} "
                        + $"({share.Subtotal} of items, the rest tax and tip)."),
                    "Replaces whatever shares the expense holds now, and every balance in "
                    + "the group moves with them."
                ],
                confirmCommand: $"groupsplit receipts divide {id} --yes");

            var applied = await client.DivideByReceiptAsync(id, ct);

            context.Output.Write(applied, RenderDivision);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Delete()
    {
        var command = new Command("delete", "Take a bill off an expense. The shares it produced stay.")
        {
            TransactionId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(TransactionId);
            var client = new Api.ReceiptsClient(context.ApiHttpClient);

            var receipt = await client.GetReceiptAsync(id, ct);

            Confirmation.Require(
                context,
                action: "receipts.delete",
                summary: $"Delete the bill of {receipt.Total} on this expense?",
                changes:
                [
                    $"{receipt.Items.Count} line(s) and their item rules go.",
                    // The shares are the ledger's and the bill is only what produced them,
                    // so removing the bill is not a refund. Said out loud because somebody
                    // deleting a receipt to "undo the split" would otherwise be surprised.
                    "The shares the expense already holds are left exactly as they are -- "
                    + "use `groupsplit transactions update` to change what people owe.",
                    // A filed bill has no row to fall back to -- filing hands it over, and
                    // the ledger keeps the record of where the expense came from.
                    "It is gone for good."
                ],
                confirmCommand: $"groupsplit receipts delete {id} --yes");

            await client.DeleteReceiptAsync(id, ct);

            context.Output.Write(
                new { status = "deleted", transactionId = id },
                _ => new Markup("[green]Deleted[/] the bill.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Who had one line, in the width of a table cell.
    /// </summary>
    /// <remarks>
    /// An empty list is called out rather than left blank, because it is the one state that
    /// stops the bill being divided -- and a blank cell in a column of names reads as a
    /// rendering gap rather than as an answer.
    /// </remarks>
    private static string Divided(ReceiptItemResponse item) => item.SplitRuleName ?? "Choose a rule";

    private static IRenderable Render(ReceiptResponse receipt)
    {
        var summary = Tables.KeyValue();
        summary.AddRow("Receipt", receipt.Id.ToString());
        summary.AddRow("Subtotal", receipt.Subtotal.ToString());
        summary.AddRow("Tax", receipt.Tax.ToString());
        summary.AddRow("Tip", receipt.Tip.ToString());
        summary.AddRow("Total", receipt.Total.ToString());

        summary.AddRow("Missing rule", receipt.MissingRuleItemCount == 0
            ? "none"
            : $"{receipt.MissingRuleItemCount} line(s)");

        // Why it cannot be divided is more useful than that it cannot, and the two reasons a
        // caller can act on are the two stated here.
        summary.AddRow("Can divide", receipt.CanDivide ? "yes" : "no -- check item rules and totals");

        var items = Tables.Grid("#", "Line", "Name", "Qty", "Price", "Tax", "Split rule");

        var position = 1;

        foreach (var item in receipt.Items)
        {
            items.AddRow(
                position++.ToString(),
                item.Id.ToString(),
                Markup.Escape(item.Name),
                item.Quantity.ToString(),
                item.TotalPrice.ToString(),
                // Only worth marking where it is not the ordinary answer. A column of
                // "taxed" down every restaurant bill says nothing.
                item.TaxAmount == 0 ? string.Empty : item.TaxAmount.ToString("0.00"),
                Divided(item));
        }

        return new Rows(summary, new Markup("\n"), items);
    }

    private static IRenderable RenderDivision(ReceiptDivisionResponse division)
    {
        var table = Tables.Grid("Owes", "Items", "Total");

        foreach (var share in division.Shares)
        {
            table.AddRow(
                share.UserId.ToString(),
                share.Subtotal.ToString(),
                share.Amount.ToString());
        }

        return new Rows(
            table,
            new Markup($"\n[grey]Each line is divided by its own rule -- its price, the tax "
                       + $"charged on it, and its share of the tip. Total {division.Total}.[/]\n"));
    }
}
