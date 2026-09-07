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

    public static Command Build()
    {
        var inbox = new Command("inbox", "Imported bank rows waiting to be filed.");

        inbox.Subcommands.Add(List());
        inbox.Subcommands.Add(Summary());
        inbox.Subcommands.Add(File());
        inbox.Subcommands.Add(Ignore());
        inbox.Subcommands.Add(Restore());

        return inbox;
    }

    private static Command List()
    {
        var status = new Option<InboxStatus?>("--status")
        {
            Description = "Which rows to show. Defaults to what is waiting."
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
            status, sortBy, order, page, pageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var rows = await new Api.InboxClient(context.ApiHttpClient).GetInboxAsync(
                status: parse.GetValue(status),
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

                var table = Tables.Grid("Id", "Spent on", "Title", "Amount", "Account", "Status");

                foreach (var row in value.Items)
                {
                    table.AddRow(
                        row.Id.ToString(),
                        row.SpentOn.ToString("yyyy-MM-dd"),
                        Markup.Escape(row.Title) + (row.Pending ? " [grey](pending)[/]" : string.Empty),
                        Tables.Money(row.Amount),
                        Markup.Escape($"{row.InstitutionName} · {row.AccountName}"),
                        Markup.Escape(row.Status.ToString().ToLowerInvariant()));
                }

                return new Rows(table, Tables.PageFooter(value));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Summary()
    {
        var command = new Command("summary", "Count the rows still waiting to be filed.");

        command.SetHandler(async (context, ct) =>
        {
            var summary = await new Api.InboxClient(context.ApiHttpClient).GetInboxSummaryAsync(ct);

            context.Output.Write(summary, value => new Markup(
                $"[bold]{value.NewCount}[/] rows waiting.\n"));

            return ExitCodes.Success;
        });

        return command;
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

        var command = new Command("file", "File an imported row as an expense.")
        {
            RowId, group, category, paidBy, splits, name, description
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
                Description = parse.GetValue(description)
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
