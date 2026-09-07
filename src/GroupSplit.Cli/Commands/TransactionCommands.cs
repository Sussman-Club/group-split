using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

public static class TransactionCommands
{
    private static readonly Option<Guid?> Group = new("--group")
    {
        Description = "Only transactions in this group."
    };

    private static readonly Option<DateTimeOffset?> From = new("--from")
    {
        Description = "Only transactions on or after this date, e.g. 2026-01-01."
    };

    private static readonly Option<DateTimeOffset?> To = new("--to")
    {
        Description = "Only transactions on or before this date."
    };

    private static readonly Option<string?> Search = new("--search")
    {
        Description = "Match against the name and description."
    };

    private static readonly Option<string?> Category = new("--category")
    {
        Description = "Only transactions in this category."
    };

    private static readonly Option<string?> SortBy = new("--sort-by")
    {
        Description = "dateTime, amount, name, category, group or paidBy. Defaults to dateTime."
    };

    private static readonly Option<SortOrder?> Order = Sorting.Order();

    private static readonly Option<int?> Page = new("--page") { Description = "1-based page number." };

    private static readonly Option<int?> PageSize = new("--page-size")
    {
        Description = "Rows per page. The server caps this."
    };

    private static readonly Argument<Guid> TransactionId = new("transaction-id")
    {
        Description = "The transaction's id."
    };

    public static Command Build()
    {
        var transactions = new Command("transactions", "Expenses and settlements.");
        transactions.Aliases.Add("tx");

        transactions.Subcommands.Add(List());
        transactions.Subcommands.Add(Show());
        transactions.Subcommands.Add(Create());
        transactions.Subcommands.Add(Update());
        transactions.Subcommands.Add(Summary());
        transactions.Subcommands.Add(Shares());
        transactions.Subcommands.Add(BankMatches());
        transactions.Subcommands.Add(Delete());

        return transactions;
    }

    private static Command List()
    {
        var command = new Command("list", "List transactions, newest first.")
        {
            Group, From, To, Search, Category, SortBy, Order, Page, PageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var page = await context.Transactions.GetTransactionsAsync(
                from: parse.GetValue(From),
                to: parse.GetValue(To),
                groupId: parse.GetValue(Group),
                paidByUserId: null,
                category: parse.GetValue(Category),
                personal: null,
                search: parse.GetValue(Search),
                sortBy: parse.GetValue(SortBy),
                sortDescending: parse.GetValue(Order).Descending(),
                page: parse.GetValue(Page),
                pageSize: parse.GetValue(PageSize),
                cancellationToken: ct);

            context.Output.Write(page, value =>
            {
                if (value.Items.Count == 0)
                {
                    return new Markup(Tables.Empty("transactions") + "\n");
                }

                var table = Tables.Grid("Id", "Date", "Name", "Amount", "Paid by", "Group");

                foreach (var transaction in value.Items)
                {
                    table.AddRow(
                        transaction.Id.ToString(),
                        transaction.DateTime.ToLocalTime().ToString("yyyy-MM-dd"),
                        Markup.Escape(transaction.Name),
                        transaction.Amount.ToString("N2"),
                        Markup.Escape(transaction.PaidByUserName),
                        Markup.Escape(transaction.GroupName ?? "-"));
                }

                return new Rows(table, Tables.PageFooter(value));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Show()
    {
        var command = new Command("show", "Show one transaction and how it was split.") { TransactionId };

        command.SetHandler(async (context, ct) =>
        {
            var transaction = await context.Transactions.GetTransactionAsync(
                context.ParseResult.GetValue(TransactionId), ct);

            context.Output.Write(transaction, value =>
            {
                var table = Tables.KeyValue();
                table.AddRow("Id", value.Id.ToString());
                table.AddRow("Name", Markup.Escape(value.Name));
                table.AddRow("Description", Markup.Escape(value.Description ?? "-"));
                table.AddRow("Amount", value.Amount.ToString("N2"));
                table.AddRow("Date", value.DateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                table.AddRow("Paid by", Markup.Escape(value.PaidByUserName));
                table.AddRow("Group", Markup.Escape(value.GroupName ?? "-"));
                table.AddRow("Category", Markup.Escape(value.Category ?? "-"));

                if (value.Splits.Count == 0)
                {
                    return table;
                }

                var splits = Tables.Grid("Owes", "Amount");

                foreach (var split in value.Splits)
                {
                    splits.AddRow(Markup.Escape(split.UserName), split.Amount.ToString("N2"));
                }

                return new Rows(table, new Markup("\n[bold]Split[/]\n"), splits);
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Create()
    {
        var name = new Argument<string>("name") { Description = "What the expense was for." };
        var amount = new Argument<decimal>("amount") { Description = "Total amount, to two decimal places." };

        var group = new Option<Guid?>("--group") { Description = "Group to book the expense in." };
        var date = new Option<DateTimeOffset?>("--date")
        {
            Description = "When it happened. Defaults to now."
        };
        var description = new Option<string?>("--description") { Description = "Free-text note." };
        var category = new Option<Guid?>("--category-id") { Description = "Category to file it under." };
        var paidBy = new Option<Guid?>("--paid-by")
        {
            Description = "Member who paid. Defaults to you."
        };
        var preview = new Option<bool>("--preview")
        {
            Description = "Show the split the server would apply without creating anything."
        };

        var command = new Command("create", "Record an expense.")
        {
            name, amount, group, date, description, category, paidBy, preview
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var request = new CreateTransactionRequest
            {
                Name = parse.GetValue(name)!,
                Amount = parse.GetValue(amount),
                DateTime = parse.GetValue(date) ?? DateTimeOffset.UtcNow,
                GroupId = parse.GetValue(group),
                CategoryId = parse.GetValue(category),
                PaidByUserId = parse.GetValue(paidBy),
                Description = parse.GetValue(description)
            };

            if (parse.GetValue(preview))
            {
                var split = await context.Transactions.PreviewTransactionSplitsAsync(request, ct);

                context.Output.Write(split, RenderSplits);

                return ExitCodes.Success;
            }

            var created = await context.Transactions.CreateTransactionAsync(request, ct);

            context.Output.Write(created, value => new Markup(
                $"[green]Created[/] {Markup.Escape(value.Name)} "
                + $"({value.Amount:N2}) [grey]{value.Id}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// An edit, sent as a JSON Patch of only what was named.
    /// </summary>
    /// <remarks>
    /// Only what was named, because the endpoint reads the patch as well as applying it:
    /// silence about the shares means "divide it again the way the category says", which is
    /// what an edit to the amount, the payer or the category should do. Sending every field
    /// every time would make <c>--name</c> quietly recompute the division -- so a flag that
    /// was not passed contributes no operation at all.
    /// </remarks>
    private static Command Update()
    {
        var name = new Option<string?>("--name") { Description = "Rename the expense." };
        var amount = new Option<decimal?>("--amount") { Description = "Change the total." };
        var date = new Option<DateTimeOffset?>("--date") { Description = "Change when it happened." };
        var description = new Option<string?>("--description") { Description = "Change the note." };
        var group = new Option<Guid?>("--group")
        {
            Description = "Move it into this group. The division is re-derived among its members."
        };
        var personal = new Option<bool>("--personal")
        {
            Description = "Take it out of its group and back onto your own ledger."
        };
        var category = new Option<Guid?>("--category-id") { Description = "File it under this category." };
        var noCategory = new Option<bool>("--no-category")
        {
            Description = "File it under nothing, so it divides evenly."
        };
        var paidBy = new Option<Guid?>("--paid-by") { Description = "Change who paid." };
        var splits = new Option<string[]>("--split")
        {
            Description = "Set the exact shares as <user-id>=<amount>, repeatable. "
                          + "Without this the division is re-derived.",
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("update", "Change an expense. Only what you name is sent.")
        {
            TransactionId, name, amount, date, description,
            group, personal, category, noCategory, paidBy, splits
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var patch = new JsonPatchDocument<UpdateTransactionRequest>();

            if (parse.GetResult(group) is not null && parse.GetValue(personal))
            {
                throw CliException.Input(
                    "--group and --personal contradict each other.",
                    "Pass one or the other.");
            }

            if (parse.GetResult(category) is not null && parse.GetValue(noCategory))
            {
                throw CliException.Input(
                    "--category-id and --no-category contradict each other.",
                    "Pass one or the other.");
            }

            if (parse.GetValue(name) is { } newName) patch.Replace(request => request.Name, newName);
            if (parse.GetValue(amount) is { } newAmount) patch.Replace(request => request.Amount, newAmount);
            if (parse.GetValue(date) is { } newDate) patch.Replace(request => request.DateTime, newDate);
            if (parse.GetValue(paidBy) is { } payer) patch.Replace(request => request.PaidByUserId, payer);

            // Read through GetResult, not the value: --description "" is a caller clearing
            // the note, and it arrives indistinguishable from absent otherwise.
            if (parse.GetResult(description) is not null)
                patch.Replace(request => request.Description, parse.GetValue(description));

            if (parse.GetValue(personal)) patch.Replace(request => request.GroupId, null);
            else if (parse.GetValue(group) is { } newGroup) patch.Replace(request => request.GroupId, newGroup);

            if (parse.GetValue(noCategory)) patch.Replace(request => request.CategoryId, null);
            else if (parse.GetValue(category) is { } newCategory)
                patch.Replace(request => request.CategoryId, newCategory);

            if (parse.GetValue(splits) is { Length: > 0 } given)
                patch.Replace(request => request.Splits, Pairs.Splits("--split", given));

            if (patch.Operations.Count == 0)
            {
                throw CliException.Input(
                    "Nothing to change.",
                    "Name at least one field, e.g. --name or --amount. "
                    + "See: groupsplit transactions update --help");
            }

            var updated = await context.Transactions.UpdateTransactionAsync(
                parse.GetValue(TransactionId), patch, ct);

            context.Output.Write(updated, value => new Markup(
                $"[green]Updated[/] {Markup.Escape(value.Name)} ({value.Amount:N2}).\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static IRenderable RenderSplits(SplitPreviewResponse preview)
    {
        var table = Tables.Grid("Member", "Share");

        foreach (var split in preview.Splits)
        {
            table.AddRow(Markup.Escape(split.UserName), split.Amount.ToString("N2"));
        }

        return new Rows(
            new Markup($"[grey]Preview only. Nothing was created.[/] "
                       + $"Rule: [bold]{Markup.Escape(preview.RuleName ?? "default")}[/]\n"),
            table);
    }

    private static Command Summary()
    {
        var command = new Command("summary", "Total the transactions matching a filter.")
        {
            Group, From, To, Search, Category
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var summary = await context.Transactions.GetTransactionsSummaryAsync(
                from: parse.GetValue(From),
                to: parse.GetValue(To),
                groupId: parse.GetValue(Group),
                paidByUserId: null,
                category: parse.GetValue(Category),
                personal: null,
                search: parse.GetValue(Search),
                cancellationToken: ct);

            context.Output.Write(summary, value => new Markup(
                $"[bold]{value.Count}[/] transactions totalling [bold]{value.Total:N2}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// What you owe a share of, rather than what you paid for.
    /// </summary>
    /// <remarks>
    /// Its own group of two commands rather than a flag on <c>list</c>, because the rows
    /// are a different shape -- each carries the whole expense and your part of it, which
    /// are two numbers -- and the summary underneath answers four figures rather than two.
    /// A flag would have had to widen both.
    /// </remarks>
    private static Command Shares()
    {
        var shares = new Command("shares", "Expenses you owe a share of, whoever paid.");

        shares.Subcommands.Add(SharesList());
        shares.Subcommands.Add(SharesSummary());

        return shares;
    }

    private static Command SharesList()
    {
        var shareSortBy = new Option<string?>("--sort-by")
        {
            Description = "dateTime, share, amount, name, category, group or paidBy. Defaults to dateTime."
        };

        var command = new Command("list", "List the expenses you owe a share of, newest first.")
        {
            Group, From, To, Search, Category, shareSortBy, Order, Page, PageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var page = await context.Transactions.GetTransactionSharesAsync(
                from: parse.GetValue(From),
                to: parse.GetValue(To),
                groupId: parse.GetValue(Group),
                paidByUserId: null,
                category: parse.GetValue(Category),
                personal: null,
                search: parse.GetValue(Search),
                sortBy: parse.GetValue(shareSortBy),
                sortDescending: parse.GetValue(Order).Descending(),
                page: parse.GetValue(Page),
                pageSize: parse.GetValue(PageSize),
                cancellationToken: ct);

            context.Output.Write(page, value =>
            {
                if (value.Items.Count == 0)
                {
                    return new Markup(Tables.Empty("shares") + "\n");
                }

                var table = Tables.Grid("Id", "Date", "Name", "Total", "Your share", "Paid by", "Group");

                foreach (var share in value.Items)
                {
                    table.AddRow(
                        share.Id.ToString(),
                        share.DateTime.ToLocalTime().ToString("yyyy-MM-dd"),
                        Markup.Escape(share.Name),
                        share.Amount.ToString("N2"),
                        share.Share.ToString("N2"),
                        // Marked rather than left to be worked out from the name: your share
                        // of something you paid for yourself is the one row here that is not
                        // a debt, and it is the whole reason the summary has two totals.
                        share.PaidByYou ? "[grey]you[/]" : Markup.Escape(share.PaidByUserName),
                        Markup.Escape(share.GroupName ?? "-"));
                }

                return new Rows(table, Tables.PageFooter(value));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command SharesSummary()
    {
        var command = new Command("summary", "Total the shares matching a filter.")
        {
            Group, From, To, Search, Category
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var summary = await context.Transactions.GetTransactionSharesSummaryAsync(
                from: parse.GetValue(From),
                to: parse.GetValue(To),
                groupId: parse.GetValue(Group),
                paidByUserId: null,
                category: parse.GetValue(Category),
                personal: null,
                search: parse.GetValue(Search),
                cancellationToken: ct);

            context.Output.Write(summary, value => new Rows(
                new Markup(
                    $"[bold]{value.Count}[/] expenses totalling [bold]{value.Total:N2}[/], "
                    + $"your share [bold]{value.Share:N2}[/]\n"),
                // Said separately, and said last, because it is the only one of the four
                // that is money owed: the rest counts what you paid for yourself, which you
                // are owed a part of rather than owing.
                new Markup(
                    $"[grey]Of that, [/][bold]{value.OwedToOthers:N2}[/][grey] is on expenses "
                    + "somebody else paid. Settlements are not counted; see "
                    + "`groupsplit users position`.[/]\n")));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The other direction of the same question: imported rows still waiting that could be
    /// an expense somebody has just typed.
    /// </summary>
    /// <remarks>
    /// Worth having as its own command because the two orders happen to different people.
    /// Somebody who records the dinner at the table asks this when the card charge lands;
    /// somebody working through the inbox asks <c>inbox matches</c>. Both answers are the
    /// same two commands, taken from whichever end you are standing at.
    /// </remarks>
    private static Command BankMatches()
    {
        var command = new Command(
            "bank-matches",
            "List imported bank rows that could be this expense arriving a second time.")
        {
            TransactionId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(TransactionId);
            var rows = await context.Transactions.GetTransactionBankMatchesAsync(id, ct);

            context.Output.Write(rows, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup("[grey]No imported row looks like this expense.[/]\n");
                }

                var table = Tables.Grid("Row", "Spent on", "Title", "Amount", "Account");

                foreach (var row in value)
                {
                    table.AddRow(
                        row.Id.ToString(),
                        row.SpentOn.ToString("yyyy-MM-dd"),
                        Markup.Escape(row.Title),
                        Tables.Money(row.Amount),
                        Markup.Escape($"{row.InstitutionName} · {row.AccountName}"));
                }

                // The expense id is known here, so it goes into the commands rather than
                // being left as a placeholder -- and each one gets its own line, because a
                // label plus a command plus a 36-character id is past the 80 columns text
                // mode renders at whenever stdout is not a terminal.
                return new Rows(
                    table,
                    new Markup(
                        "\n[grey]Same payment:[/]\n"
                        + $"[grey]  groupsplit inbox link <row-id> {id}[/]\n"
                        + "[grey]Not a match:[/]\n"
                        + $"[grey]  groupsplit inbox dismiss-match <row-id> {id}[/]\n"));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Delete()
    {
        var command = new Command("delete", "Delete a transaction.") { TransactionId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(TransactionId);
            var transaction = await context.Transactions.GetTransactionAsync(id, ct);

            Confirmation.Require(
                context,
                action: "transactions.delete",
                summary: $"Delete '{transaction.Name}'?",
                changes:
                [
                    $"'{transaction.Name}' for {transaction.Amount:N2} is removed.",
                    "Every member's balance in the group is recalculated."
                ],
                confirmCommand: $"groupsplit transactions delete {id} --yes");

            await context.Transactions.DeleteTransactionAsync(id, ct);

            context.Output.Write(
                new { status = "deleted", transactionId = id, name = transaction.Name },
                value => new Markup($"[green]Deleted[/] {Markup.Escape(value.name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
