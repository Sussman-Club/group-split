using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// The rows a bank sync brought in, and what a person does with them.
/// </summary>
/// <remarks>
/// The same four decisions the inbox page offers -- look, add, ignore, put back -- because
/// the CLI is not a subset of the app. The span options exist for the reason the page's
/// chips do: a bank sends months at a time, and the way through a backlog is a month of it
/// at a time. Dates are days rather than instants here, since that is what a bank puts on a
/// row: a statement date is a calendar date, not a moment in somebody's zone.
/// </remarks>
public static class InboxCommands
{
    private static readonly Option<InboxStatus?> Status = new("--status")
    {
        Description = "Which rows to show: New, Filed or Ignored. Defaults to New."
    };

    private static readonly Option<DateOnly?> From = new("--from")
    {
        Description = "Only rows on or after this day, e.g. 2026-01-01."
    };

    private static readonly Option<DateOnly?> To = new("--to")
    {
        Description = "Only rows on or before this day."
    };

    private static readonly Option<int?> Page = new("--page") { Description = "1-based page number." };

    private static readonly Option<int?> PageSize = new("--page-size")
    {
        Description = "Rows per page. The server caps this."
    };

    private static readonly Argument<Guid> RowId = new("row-id")
    {
        Description = "The imported row's id."
    };

    public static Command Build()
    {
        var inbox = new Command("inbox", "Transactions your bank sent, waiting to be dealt with.");

        inbox.Subcommands.Add(List());
        inbox.Subcommands.Add(Add());
        inbox.Subcommands.Add(Ignore());
        inbox.Subcommands.Add(Restore());

        return inbox;
    }

    private static Command List()
    {
        var command = new Command("list", "List imported rows, newest first.")
        {
            Status, From, To, Page, PageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var page = await new Api.InboxClient(context.ApiHttpClient).GetInboxAsync(
                status: parse.GetValue(Status),
                from: parse.GetValue(From),
                to: parse.GetValue(To),
                sortBy: null,
                sortDescending: null,
                page: parse.GetValue(Page),
                pageSize: parse.GetValue(PageSize),
                cancellationToken: ct);

            context.Output.Write(page, value =>
            {
                if (value.Items.Count == 0)
                {
                    return new Markup(Tables.Empty("imported transactions") + "\n");
                }

                var table = Tables.Grid("Id", "Date", "Description", "Amount", "Account", "Status");

                foreach (var row in value.Items)
                {
                    table.AddRow(
                        row.Id.ToString(),
                        row.SpentOn.ToString("yyyy-MM-dd"),
                        Markup.Escape(row.Title),
                        // Money coming in is signed, so a refund does not read as a charge.
                        (row.IsCredit ? "+" : "") + Math.Abs(row.Amount).ToString("N2") + " " + row.Currency,
                        Markup.Escape(row.AccountName),
                        row.Status.ToString());
                }

                return new Rows(
                    table,
                    new Markup(
                        $"[grey]Page {value.Page} of "
                        + $"{Math.Max(1, (int)Math.Ceiling(value.TotalCount / (double)Math.Max(1, value.PageSize)))}, "
                        + $"{value.TotalCount} total.[/]\n"));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Turns a row into an expense. Named "add" rather than "file" to match what the page's
    /// button says, since they are the same decision.
    /// </summary>
    private static Command Add()
    {
        var group = new Option<Guid?>("--group")
        {
            Description = "The group to add it to. Left out, it becomes one of your own expenses."
        };

        var category = new Option<Guid?>("--category") { Description = "The category to file it under." };

        var paidBy = new Option<Guid?>("--paid-by")
        {
            Description = "Who paid. Defaults to you."
        };

        var name = new Option<string?>("--name")
        {
            Description = "Override the expense's name. Defaults to what the bank called it."
        };

        var description = new Option<string?>("--description") { Description = "A note on the expense." };

        var command = new Command("add", "Add an imported row to a group as an expense.")
        {
            RowId, group, category, paidBy, name, description
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(RowId);

            var expense = await new Api.InboxClient(context.ApiHttpClient).FileBankTransactionAsync(
                id,
                new FileBankTransactionRequest
                {
                    GroupId = parse.GetValue(group),
                    CategoryId = parse.GetValue(category),
                    PaidByUserId = parse.GetValue(paidBy),
                    Name = parse.GetValue(name),
                    Description = parse.GetValue(description)
                },
                ct);

            context.Output.Write(
                new { status = "added", rowId = id, transactionId = expense.Id, name = expense.Name },
                value => new Markup(
                    $"[green]Added[/] {Markup.Escape(value.name)} as expense {value.transactionId}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Ignore()
    {
        var command = new Command("ignore", "Take a row out of the inbox without making anything of it.")
        {
            RowId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(RowId);

            Confirmation.Require(
                context,
                action: "inbox.ignore",
                summary: $"Ignore imported row {id}?",
                changes:
                [
                    "The row leaves the inbox and becomes no expense.",
                    "It can be put back with 'groupsplit inbox restore'."
                ],
                confirmCommand: $"groupsplit inbox ignore {id} --yes");

            await new Api.InboxClient(context.ApiHttpClient).IgnoreBankTransactionAsync(id, ct);

            context.Output.Write(
                new { status = "ignored", rowId = id },
                value => new Markup($"[green]Ignored[/] row {value.rowId}.\n"));

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
                value => new Markup($"[green]Put back[/] row {value.rowId}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
