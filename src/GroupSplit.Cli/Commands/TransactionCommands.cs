using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
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
        transactions.Subcommands.Add(Summary());
        transactions.Subcommands.Add(Delete());

        return transactions;
    }

    private static Command List()
    {
        var command = new Command("list", "List transactions, newest first.")
        {
            Group, From, To, Search, Category, Page, PageSize
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
                sortBy: null,
                sortDescending: null,
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
