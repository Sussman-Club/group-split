using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// The rows a bank sync brought in, and what a person does with them: file one as an
/// expense, ignore it, put it back.
/// </summary>
public static class InboxCommands
{
    private static readonly Argument<Guid> RowId = new("row-id")
    {
        Description = "The imported row's id, as shown by `groupsplit inbox list`."
    };

    private static readonly Argument<Guid> TransactionId = new("transaction-id")
    {
        Description = "The expense's id, as shown by `groupsplit inbox matches`."
    };

    public static Command Build()
    {
        var inbox = new Command("inbox", "Imported bank rows waiting to be filed.");

        inbox.Subcommands.Add(List());
        inbox.Subcommands.Add(Summary());
        inbox.Subcommands.Add(Matches());
        inbox.Subcommands.Add(File());
        inbox.Subcommands.Add(Link());
        inbox.Subcommands.Add(DismissMatch());
        inbox.Subcommands.Add(Ignore());
        inbox.Subcommands.Add(Restore());

        return inbox;
    }

    private static Command List()
    {
        var status = new Option<InboxStatus?>("--status")
        {
            Description = "Which rows to show. Defaults to what is waiting."
        }.WithDescribedValues();

        // Days rather than instants, because that is what a bank puts on a row: a statement
        // date is a calendar date the bank decided on, not a moment in anybody's zone. A
        // sync brings in months at a time, and the way through a backlog is a month of it.
        var from = new Option<DateOnly?>("--from")
        {
            Description = "Only rows on or after this day, e.g. 2026-01-01."
        };

        var to = new Option<DateOnly?>("--to")
        {
            Description = "Only rows on or before this day."
        };

        var sortBy = new Option<string?>("--sort-by")
        {
            Description = "date, amount or merchant. Defaults to date."
        };

        var order = Sorting.Order();

        var page = new Option<int?>("--page") { Description = "1-based page number." };
        var pageSize = new Option<int?>("--page-size")
        {
            Description = "Rows per page. The server caps this."
        };

        var command = new Command("list", "List imported rows, newest first.")
        {
            status, from, to, sortBy, order, page, pageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var rows = await new Api.InboxClient(context.ApiHttpClient).GetInboxAsync(
                status: parse.GetValue(status),
                from: parse.GetValue(from),
                to: parse.GetValue(to),
                sortBy: parse.GetValue(sortBy),
                sortDescending: parse.GetValue(order).Descending(),
                page: parse.GetValue(page),
                pageSize: parse.GetValue(pageSize),
                cancellationToken: ct);

            context.Output.Write(rows, value =>
            {
                if (value.Items.Count == 0)
                {
                    return new Markup(Tables.Empty("imported rows") + "\n");
                }

                // No status column: every row on a page shares one, because the status is
                // what the listing was filtered by. It would be 10 columns of nothing on a
                // table already wide enough to wrap around a row id.
                var table = Tables.Grid("Id", "Spent on", "Title", "Amount", "Account");

                foreach (var row in value.Items)
                {
                    table.AddRow(
                        row.Id.ToString(),
                        row.SpentOn.ToString("yyyy-MM-dd"),
                        Title(row),
                        Tables.Money(row.Amount),
                        Markup.Escape($"{row.InstitutionName} · {row.AccountName}"));
                }

                // The listing already carries the suggestions, so saying so costs nothing
                // here -- and filing one of these without an answer would be refused, which
                // is a worse way to find out.
                //
                // Two counts, because they are two different claims. A confident match is
                // the same money to the cent; a possible one is a figure within a quarter of
                // it, which lands on unrelated money often enough that calling it "already
                // recorded" would be wrong most times it was said. Both are refused a
                // filing, so both are worth knowing about before trying.
                var recorded = value.Items.Count(row => row.PossibleDuplicates.Any(match => match.IsConfident));
                var could = value.Items.Count(row =>
                    row.PossibleDuplicates.Count > 0 && !row.PossibleDuplicates.Any(match => match.IsConfident));

                return new Rows(
                    table,
                    recorded == 0 && could == 0
                        ? new Markup(string.Empty)
                        : new Markup(
                            (recorded > 0 ? $"[yellow]{recorded}[/] may already be recorded" : string.Empty)
                            + (recorded > 0 && could > 0 ? ", and " : string.Empty)
                            + (could > 0 ? $"[grey]{could}[/] could be" : string.Empty)
                            + ". Filing either is refused until answered.\n"
                            + "[grey]  groupsplit inbox matches <row-id>[/]\n"),
                    Tables.PageFooter(value));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// What the row is, and the two things about it that change what to do next: a pending
    /// row will be replaced by its posted one, and a row a suggestion hangs on cannot be
    /// filed without answering the suggestion first.
    /// </summary>
    /// <remarks>
    /// Marked on the title rather than in a column of its own, because that is where the
    /// eye already is and because this table is wide enough as it is -- text mode is 80
    /// columns whenever stdout is not a terminal, which is every redirected run.
    /// <para>
    /// The mark says which grade it is. <c>(duplicate?)</c> claims the row is one, and a
    /// mark that claims it wrongly most times it appears is a mark people stop reading --
    /// which is also how a real duplicate gets waved through. A possible match gets the
    /// quieter <c>(similar)</c>, because that is all the rule actually found.
    /// </para>
    /// </remarks>
    private static string Title(BankTransactionResponse row)
        => Markup.Escape(row.Title)
           + (row.Pending ? " [grey](pending)[/]" : string.Empty)
           + Mark(row.PossibleDuplicates);

    private static string Mark(IReadOnlyList<ExpenseMatchResponse> matches) => matches switch
    {
        { Count: 0 } => string.Empty,
        _ when matches.Any(match => match.IsConfident) => " [yellow](duplicate?)[/]",
        _ => " [grey](similar)[/]"
    };

    private static Command Summary()
    {
        var command = new Command("summary", "Count the rows still waiting to be filed.");

        // Working out how many of those look like an expense already recorded means running
        // the matcher over every waiting row, so the server only does it when asked. Asked
        // for by default here: this command exists to be read once, not polled.
        //
        // What comes back is the count of *confident* matches -- rows whose amount agrees to
        // the cent with something already recorded. Rows that merely sit close are not in
        // it, deliberately: a single number read as "some of these are already recorded"
        // has to be right about it. `inbox list` says how many of those there are.
        var duplicates = new Option<bool>("--duplicates")
        {
            Description = "Also count the rows that may already be recorded as an expense.",
            DefaultValueFactory = _ => true
        };

        command.Add(duplicates);

        command.SetHandler(async (context, ct) =>
        {
            var wanted = context.ParseResult.GetValue(duplicates);

            var summary = await new Api.InboxClient(context.ApiHttpClient)
                .GetInboxSummaryAsync(wanted ? true : null, ct);

            context.Output.Write(summary, value => new Markup(
                $"[bold]{value.NewCount}[/] rows waiting"
                + (value.PossibleDuplicates is { } possible
                    ? $", [bold]{possible}[/] already recorded to the cent"
                    : string.Empty)
                + ".\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The expenses already recorded that a row could be the same money as.
    /// </summary>
    /// <remarks>
    /// A suggestion and nothing more: nothing here merges, hides, files or ignores
    /// anything. It exists because both answers to the refusal need an expense id, and the
    /// refusal is the only other place they appear.
    /// </remarks>
    private static Command Matches()
    {
        var command = new Command(
            "matches",
            "List the expenses already recorded that an imported row could be.")
        {
            RowId
        };

        command.SetHandler(async (context, ct) =>
        {
            var matches = await new Api.InboxClient(context.ApiHttpClient)
                .GetBankTransactionMatchesAsync(context.ParseResult.GetValue(RowId), ct);

            var rowId = context.ParseResult.GetValue(RowId);

            context.Output.Write(matches, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(
                        "[grey]Nothing already recorded looks like this row.[/]\n"
                        + $"[grey]  groupsplit inbox file {rowId}[/]\n");
                }

                // A block each rather than a table: there are at most three of these, the
                // id is 36 characters, and a table wide enough to hold it wraps the id in
                // half at 80 columns -- which is the one thing here that has to be
                // copyable.
                var blocks = new List<IRenderable>();

                foreach (var match in value)
                {
                    blocks.Add(new Markup(
                        $"[bold]{Markup.Escape(match.Name)}[/] {match.Amount:N2} "
                        + $"{Markup.Escape(match.Currency)} in {Markup.Escape(match.Where)}"
                        + $" {Grade(match)}\n"
                        + $"  [grey]paid by {Markup.Escape(match.PaidByUserName)}, "
                        + $"{Markup.Escape(Apart(match))}[/]\n"
                        + $"  [cyan]{match.TransactionId}[/]\n\n"));
                }

                // Placeholders rather than this row's id interpolated in: an id is 36
                // characters, and a label plus a command plus one of them is past 80. The
                // id was just typed to get here, and the expense ids are listed above.
                blocks.Add(new Markup(
                    "[grey]Same payment:[/] groupsplit inbox link <row-id> <transaction-id>\n"
                    + "[grey]Paid twice:[/]   groupsplit inbox file <row-id> --file-anyway\n"
                    + "[grey]Not a match:[/]  groupsplit inbox dismiss-match <row-id> <transaction-id>\n"));

                return new Rows(blocks);
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Which grade a candidate is, said on its own line rather than left to be inferred from
    /// the numbers underneath it.
    /// </summary>
    /// <remarks>
    /// The reason this is spelled out at all is that an agent reads this output too, and one
    /// that cannot tell a confident match from a coincidence has to treat every candidate as
    /// a stop -- which is how the guard turns into a formality that gets waved through. The
    /// words are the ones the inbox uses, so nobody learns them twice.
    /// </remarks>
    private static string Grade(ExpenseMatchResponse match) =>
        match.IsConfident ? "[yellow](same amount)[/]" : "[grey](similar amount)[/]";

    /// <summary>
    /// How far apart the two are on the axes that decided the suggestion, so a person can
    /// see why it was offered rather than take it on trust.
    /// </summary>
    private static string Apart(ExpenseMatchResponse match)
    {
        var days = match.DaysApart == 1 ? "1 day" : $"{match.DaysApart} days";

        return match.AmountDifference == 0
            ? $"{days}, same amount"
            : $"{days}, {match.AmountDifference:N2} apart";
    }

    /// <summary>
    /// Turns a row into an expense. What the bank already said -- the date, the amount, the
    /// currency -- has no flag here, because filing copies it; correcting the bank is
    /// <c>transactions update</c> afterwards.
    /// </summary>
    private static Command File()
    {
        var group = new Option<Guid?>("--group")
        {
            Description = "Group to file it in. Omit to keep it personal."
        };

        var category = new Option<Guid?>("--category-id")
        {
            Description = "Category to file it under. Its rule divides the expense."
        };

        var paidBy = new Option<Guid?>("--paid-by")
        {
            Description = "Member who paid, when it was not you. Only meaningful in a group."
        };

        var splits = new Option<string[]>("--split")
        {
            Description = "Exact shares as <user-id>=<amount>, repeatable. "
                          + "Omit to divide it the way the category says.",
            AllowMultipleArgumentsPerToken = true
        };

        var name = new Option<string?>("--name")
        {
            Description = "What to call the expense. Defaults to the merchant the bank named."
        };

        var description = new Option<string?>("--description") { Description = "Free-text note." };

        var fileAnyway = new Option<bool>("--file-anyway")
        {
            Description = "Record it even though an expense already there looks like the "
                          + "same payment. You really did pay twice."
        };

        var command = new Command("file", "File an imported row as an expense.")
        {
            RowId, group, category, paidBy, splits, name, description, fileAnyway
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var given = parse.GetValue(splits) ?? [];

            var request = new FileBankTransactionRequest
            {
                GroupId = parse.GetValue(group),
                CategoryId = parse.GetValue(category),
                PaidByUserId = parse.GetValue(paidBy),
                // Null, not empty: an empty list is "divide it between nobody", which the
                // API refuses, while null is "divide it the way the category says".
                Splits = given.Length == 0 ? null : Pairs.Splits("--split", given),
                Name = parse.GetValue(name),
                Description = parse.GetValue(description),
                // Never defaulted true for convenience: the refusal is what stops a second
                // expense coming into being before anybody has been told about the first,
                // and this flag is the person saying they were told.
                FileAnyway = parse.GetValue(fileAnyway)
            };

            var expense = await new Api.InboxClient(context.ApiHttpClient)
                .FileBankTransactionAsync(parse.GetValue(RowId), request, ct);

            context.Output.Write(expense, value => new Markup(
                $"[green]Filed[/] {Markup.Escape(value.Name)} ({value.Amount:N2}) "
                + $"in {Markup.Escape(value.GroupName ?? "your own ledger")} [grey]{value.Id}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Attaches the row to an expense that is already there, rather than making a second
    /// one for the same money.
    /// </summary>
    /// <remarks>
    /// What comes back is that expense, now carrying the bank's row: the amount, the date
    /// and the division are untouched. A card settling for six more than the receipt is not
    /// a correction anybody asked for, so this does not make one.
    /// </remarks>
    private static Command Link()
    {
        var command = new Command(
            "link",
            "Attach an imported row to an expense already recorded, instead of filing a second one.")
        {
            RowId, TransactionId
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var transactionId = parse.GetValue(TransactionId);

            var expense = await new Api.InboxClient(context.ApiHttpClient).LinkBankTransactionAsync(
                parse.GetValue(RowId),
                new LinkBankTransactionRequest { TransactionId = transactionId },
                ct);

            context.Output.Write(expense, value => new Markup(
                $"[green]Attached[/] to {Markup.Escape(value.Name)} ({value.Amount:N2}).\n"
                + "[grey]Nothing about the expense changed.[/]\n"
                + $"[grey]{value.Id}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Says the two are not the same money after all. The pair is then never suggested
    /// again -- a suggestion nobody can get rid of being worse than none.
    /// </summary>
    private static Command DismissMatch()
    {
        var command = new Command(
            "dismiss-match",
            "Say an imported row and a suggested expense are not the same money.")
        {
            RowId, TransactionId
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var rowId = parse.GetValue(RowId);
            var transactionId = parse.GetValue(TransactionId);

            // No confirmation: it removes a suggestion, not a record, and the row is still
            // there to file or ignore afterwards.
            await new Api.InboxClient(context.ApiHttpClient).DismissBankTransactionMatchAsync(
                rowId, new DismissBankMatchRequest { TransactionId = transactionId }, ct);

            context.Output.Write(
                new { status = "dismissed", rowId, transactionId },
                _ => new Markup(
                    "[green]Dismissed.[/] [grey]That pair will not be suggested again.[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Ignore()
    {
        var command = new Command("ignore", "Keep a row out of the inbox without filing it.")
        {
            RowId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(RowId);

            // No confirmation: ignoring is undone by `inbox restore`, so nothing is lost.
            await new Api.InboxClient(context.ApiHttpClient).IgnoreBankTransactionAsync(id, ct);

            context.Output.Write(
                new { status = "ignored", rowId = id },
                _ => new Markup("[green]Ignored.[/] [grey]Undo with: groupsplit inbox restore[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Restore()
    {
        var command = new Command("restore", "Put an ignored row back in the inbox.") { RowId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(RowId);

            await new Api.InboxClient(context.ApiHttpClient).RestoreBankTransactionAsync(id, ct);

            context.Output.Write(
                new { status = "restored", rowId = id },
                _ => new Markup("[green]Restored.[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
