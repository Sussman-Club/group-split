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
/// The tax and the tip are apportioned in proportion to what each person claimed, which is
/// the only division of them that does not depend on who ordered the expensive thing.
/// </para>
/// </remarks>
public static class ReceiptCommands
{
    private static readonly Argument<Guid> TransactionId = new("transaction-id")
    {
        Description = "The expense's id, as shown by `groupsplit transactions list`."
    };

    private static Option<bool> BankRow() => new("--bank-row")
    {
        Description = "Read the id as an imported bank row's, from `groupsplit inbox list`, "
                      + "rather than an expense's. Lets a dinner be itemised at the table "
                      + "before anybody files the card charge."
    };

    public static Command Build()
    {
        var receipts = new Command("receipts",
            "Itemised bills: what was on them, who had what, and dividing by it.");

        receipts.Subcommands.Add(Show());
        receipts.Subcommands.Add(Set());
        receipts.Subcommands.Add(Claim());
        receipts.Subcommands.Add(Preview());
        receipts.Subcommands.Add(Divide());
        receipts.Subcommands.Add(Split());
        receipts.Subcommands.Add(Delete());

        return receipts;
    }

    private static Command Show()
    {
        var bankRow = BankRow();

        var command = new Command("show", "Show a bill, its lines, and who has claimed them.")
        {
            TransactionId, bankRow
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(TransactionId);
            var client = new Api.ReceiptsClient(context.ApiHttpClient);

            var receipt = context.ParseResult.GetValue(bankRow)
                ? await client.GetBankRowReceiptAsync(id, ct)
                : await client.GetReceiptAsync(id, ct);

            context.Output.Write(receipt, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Set()
    {
        var items = new Option<string[]>("--item")
        {
            Description = "A line on the bill, as <name>=<price>[x<qty>][/notax][@<user-id>[*<weight>],...], "
                          + "repeatable. /notax says the bill's tax was not charged on it. "
                          + "Claimants are optional here and can be set later with "
                          + "`groupsplit receipts claim`.",
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

        var bankRow = BankRow();

        var command = new Command("set",
            "Transcribe a bill, replacing whatever was there. Does not divide anything.")
        {
            TransactionId, items, subtotal, tax, tip, total, bankRow
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

            var receipt = parse.GetValue(bankRow)
                ? await client.SaveBankRowReceiptAsync(id, request, ct)
                : await client.SaveReceiptAsync(id, request, ct);

            context.Output.Write(receipt, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Claim()
    {
        var itemId = new Argument<Guid>("item-id")
        {
            Description = "The line's id, as shown by `groupsplit receipts show`."
        };

        var users = new Option<string[]>("--user")
        {
            Description = "Who had it, as <user-id>[*<weight>], repeatable. "
                          + "Pass none to un-claim the line.",
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("claim", "Say who had one line of the bill.")
        {
            TransactionId, itemId, users
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var named = parse.GetValue(users) ?? [];

            // Through the item parser, so one line's claimants read exactly as they do
            // inside --item and a weight means the same thing in both places.
            var claims = named.Length > 0
                ? ReceiptItems.Parse("--user", [$"line={0m}@{string.Join(',', named)}"]).Single().Claims
                : [];

            var receipt = await new Api.ReceiptsClient(context.ApiHttpClient)
                .SetReceiptItemClaimsAsync(
                    parse.GetValue(TransactionId),
                    parse.GetValue(itemId),
                    new SetReceiptItemClaimsRequest { Claims = claims },
                    ct);

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
                        + $"({share.ClaimedSubtotal} of items, the rest tax and tip)."),
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

    /// <summary>
    /// Files one imported charge as several expenses, by saying which lines are which.
    /// </summary>
    /// <remarks>
    /// The other half of the feature, at the other scale. <c>divide</c> splits one expense
    /// between the people who had each line; this splits one charge between the purchases it
    /// turns out to be -- the flat's groceries and a jacket that is nobody's business but
    /// yours, on one warehouse receipt.
    /// <para>
    /// Everything at once. Every line has to land in exactly one part, so there is no
    /// half-split row to come back to and nothing to reconcile: either every part exists or
    /// the charge is still waiting.
    /// </para>
    /// <para>
    /// The amounts are not given and cannot be. Each part is cut from the charge in
    /// proportion to the lines it holds, with the tax and the tip apportioned over them, so
    /// the parts sum to what the card was charged by construction rather than by the caller
    /// getting the arithmetic right.
    /// </para>
    /// </remarks>
    private static Command Split()
    {
        var rowId = new Argument<Guid>("bank-transaction-id")
        {
            Description = "The imported row's id, as shown by `groupsplit inbox list`."
        };

        var parts = new Option<string[]>("--part")
        {
            Description = "One purchase, as <name>=<lines>[@<group-id>[/<category-id>]], "
                          + "repeatable and needed at least twice. <lines> is line numbers, "
                          + "ranges or ids from `groupsplit receipts show --bank-row`; no "
                          + "@group keeps that part on your own ledger.",
            AllowMultipleArgumentsPerToken = false
        };

        var fileAnyway = new Option<bool>("--file-anyway")
        {
            Description = "Go ahead even though this charge looks like an expense already "
                          + "recorded."
        };

        var command = new Command("split",
            "File one imported charge as several expenses, by its bill.")
        {
            rowId, parts, fileAnyway
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(rowId);

            if (parse.GetValue(parts) is not { Length: > 1 } given)
            {
                throw CliException.Input(
                    "Splitting a charge needs at least two --part.",
                    "One part is an ordinary filing -- use `groupsplit inbox file`. Try "
                    + "--part \"Groceries=1-4@<group-id>\" --part \"Clothes=5,6\".");
            }

            // Read first, because a part names lines by where they are on the paper and only
            // the bill knows what is there. It also means a position that is not on the bill
            // is refused before anything is created, naming the number the caller typed
            // rather than an id they never saw.
            var bill = await new Api.ReceiptsClient(context.ApiHttpClient)
                .GetBankRowReceiptAsync(id, ct);

            var request = new SplitBankTransactionRequest
            {
                Parts = ReceiptParts.Parse("--part", given, bill.Items),
                // Never defaulted true for convenience. A split files several expenses at
                // once, so going ahead over a payment already recorded is several wrong
                // balances rather than one, and this flag is the person saying they were
                // told.
                FileAnyway = parse.GetValue(fileAnyway)
            };

            var split = await new Api.InboxClient(context.ApiHttpClient)
                .SplitBankTransactionAsync(id, request, ct);

            context.Output.Write(split, RenderSplit);

            return ExitCodes.Success;
        });

        return command;
    }

    private static IRenderable RenderSplit(SplitBankTransactionResponse split)
    {
        var table = Tables.Grid("Expense", "Name", "Lines", "Amount");

        foreach (var part in split.Parts)
        {
            table.AddRow(
                part.TransactionId.ToString(),
                Markup.Escape(part.Name),
                part.ItemCount.ToString(),
                part.Amount.ToString());
        }

        return new Rows(
            new Markup($"[green]Split[/] {split.Charge} into {split.Parts.Count} expenses.\n\n"),
            table,
            // The invariant, said once where it can be checked: the parts are cut from the
            // charge, so this is arithmetic the caller can follow rather than trust.
            new Markup($"\n[grey]Tax and tip are apportioned between the parts in proportion "
                       + $"to the lines each holds. The parts come to "
                       + $"{split.Parts.Sum(part => part.Amount)}, against a charge of "
                       + $"{split.Charge}.[/]\n"));
    }

    private static Command Delete()
    {
        var bankRow = BankRow();

        var command = new Command("delete", "Take a bill off an expense, or off an imported row.")
        {
            TransactionId, bankRow
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(TransactionId);
            var client = new Api.ReceiptsClient(context.ApiHttpClient);

            // A bill on a row is the simpler act: nothing has been divided by it, so there are
            // no shares to reassure anybody about. It is also the only way to be rid of one
            // that filing declined to take -- a row filed into a personal expense keeps its
            // bill, and file and link both refuse a row that is already filed.
            if (context.ParseResult.GetValue(bankRow))
            {
                var onTheRow = await client.GetBankRowReceiptAsync(id, ct);

                Confirmation.Require(
                    context,
                    action: "receipts.delete",
                    summary: $"Delete the bill of {onTheRow.Total} on this imported row?",
                    changes:
                    [
                        $"{onTheRow.Items.Count} line(s) and every claim on them go.",
                        "Nothing has been divided by it, so no balance moves."
                    ],
                    confirmCommand: $"groupsplit receipts delete {id} --bank-row --yes");

                await client.DeleteBankRowReceiptAsync(id, ct);

                context.Output.Write(
                    new { status = "deleted", bankTransactionId = id },
                    _ => new Markup("[green]Deleted[/] the bill.\n"));

                return ExitCodes.Success;
            }

            var receipt = await client.GetReceiptAsync(id, ct);

            Confirmation.Require(
                context,
                action: "receipts.delete",
                summary: $"Delete the bill of {receipt.Total} on this expense?",
                changes:
                [
                    $"{receipt.Items.Count} line(s) and every claim on them go.",
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
    private static string Divided(ReceiptItemResponse item) => item.Claims.Count == 0
        ? "[yellow]nobody[/]"
        : Markup.Escape(string.Join(", ", item.Claims.Select(claim =>
            claim.Weight == 1
                ? $"{claim.UserId} ({claim.Share})"
                : $"{claim.UserId}*{claim.Weight} ({claim.Share})")));

    private static IRenderable Render(ReceiptResponse receipt)
    {
        var summary = Tables.KeyValue();
        summary.AddRow("Receipt", receipt.Id.ToString());
        summary.AddRow("Subtotal", receipt.Subtotal.ToString());
        summary.AddRow("Tax", receipt.Tax.ToString());
        summary.AddRow("Tip", receipt.Tip.ToString());
        summary.AddRow("Total", receipt.Total.ToString());

        summary.AddRow("Unclaimed", receipt.UnclaimedItemCount == 0
            ? "none"
            : $"{receipt.UnclaimedItemCount} line(s)");

        // Why it cannot be divided is more useful than that it cannot, and the two reasons a
        // caller can act on are the two stated here.
        summary.AddRow("Can divide", receipt.CanDivide
            ? "yes"
            : receipt.ExpenseId is null
                ? "no -- not filed as an expense yet"
                : "no -- see Unclaimed, or the figures do not add up");

        // The position first, because that is what `receipts split --part` refers to: a
        // warehouse bill is twenty lines and nobody is pasting twenty guids. The id stays
        // beside it for anything that wants to be unambiguous.
        var items = Tables.Grid("#", "Line", "Name", "Qty", "Price", "Tax", "Had by");

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
                item.IsTaxable ? string.Empty : "[grey]exempt[/]",
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
                share.ClaimedSubtotal.ToString(),
                share.Amount.ToString());
        }

        return new Rows(
            table,
            new Markup($"\n[grey]Tax and tip are shared out in proportion to what each "
                       + $"person claimed. Total {division.Total}.[/]\n"));
    }
}
