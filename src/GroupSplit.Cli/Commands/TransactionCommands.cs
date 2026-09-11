using System.CommandLine;
using System.Net;
using GroupSplit.Cli.Api;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

public static class TransactionCommands
{
    /// <summary>
    /// The group option, made once per command rather than shared between them.
    /// </summary>
    /// <remarks>
    /// One instance used to be added to every command that filters by group, which meant
    /// one description covering commands that do two different things with it: on
    /// <c>list</c> and <c>summary</c> it selects a group's whole ledger, and on
    /// <c>monthly</c> and the two <c>shares</c> commands it narrows the caller's own rows
    /// to one group. A single wording was wrong for one of those two, and the wrong one is
    /// what the schema published.
    /// </remarks>
    private static Option<Guid?> GroupOption(string description) =>
        new("--group") { Description = description };

    /// <summary>
    /// On the two commands that read a group's ledger: every expense in it, whoever paid,
    /// which is what the option always claimed and never did.
    /// </summary>
    private const string GroupIsTheLedger =
        "Read this group's whole ledger instead of your own: every expense in it, whoever paid.";

    /// <summary>
    /// On the commands whose rows are the caller's by construction, where naming a group
    /// can only narrow them.
    /// </summary>
    private const string GroupNarrowsYours = "Only your own rows in this group.";

    /// <summary>
    /// Who paid, for asking about one person's spending deliberately. The filter has
    /// always existed in the request contract and always been honoured; nothing set it,
    /// and <c>--group</c> was quietly doing it instead.
    /// </summary>
    private static readonly Option<Guid?> PaidBy = new("--paid-by")
    {
        Description = "Only expenses this user paid for. Ids come from `groupsplit groups members <group-id>`."
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
        transactions.Subcommands.Add(Monthly());
        transactions.Subcommands.Add(Shares());
        transactions.Subcommands.Add(BankMatches());
        transactions.Subcommands.Add(Reattach());
        transactions.Subcommands.Add(Delete());

        return transactions;
    }

    /// <summary>
    /// The expenses the caller paid for, or -- with <c>--group</c> -- a group's whole
    /// ledger.
    /// </summary>
    /// <remarks>
    /// <c>--group</c> used to be sent as a filter to the personal listing, which narrows to
    /// the payer on top of whatever filter it was given. So a group's listing came back
    /// holding only the rows the caller had paid for, under an option documented as a group
    /// filter: a member who had paid for 2 of a group's 1,411 expenses was told the group
    /// held 2, and believed it -- an empty-looking answer to a plain question is
    /// indistinguishable from the truth.
    /// <para>
    /// The group's own listing has always existed, takes the same filters, sort and paging,
    /// and is scoped to membership rather than to the payer. Reading it is the whole fix.
    /// </para>
    /// </remarks>
    private static Command List()
    {
        var group = GroupOption(GroupIsTheLedger);

        var command = new Command("list",
            "List the expenses you paid for, newest first. With --group, the group's whole ledger.")
        {
            group, PaidBy, From, To, Search, Category, SortBy, Order, Page, PageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var groupId = parse.GetValue(group);
            var paidBy = parse.GetValue(PaidBy);

            // The group id travels in the path rather than as a filter beside it: the
            // endpoint is already about that group, and sending it twice would be one
            // value narrowing the same set twice.
            var page = groupId is { } id
                ? await context.Groups.GetGroupTransactionsAsync(
                    id: id,
                    from: parse.GetValue(From),
                    to: parse.GetValue(To),
                    groupId: null,
                    paidByUserId: paidBy,
                    category: parse.GetValue(Category),
                    personal: null,
                    search: parse.GetValue(Search),
                    sortBy: parse.GetValue(SortBy),
                    sortDescending: parse.GetValue(Order).Descending(),
                    page: parse.GetValue(Page),
                    pageSize: parse.GetValue(PageSize),
                    cancellationToken: ct)
                : await context.Transactions.GetTransactionsAsync(
                    from: parse.GetValue(From),
                    to: parse.GetValue(To),
                    groupId: null,
                    paidByUserId: paidBy,
                    category: parse.GetValue(Category),
                    personal: null,
                    search: parse.GetValue(Search),
                    sortBy: parse.GetValue(SortBy),
                    sortDescending: parse.GetValue(Order).Descending(),
                    page: parse.GetValue(Page),
                    pageSize: parse.GetValue(PageSize),
                    cancellationToken: ct);

            // Asked only when there is nothing to render: an empty group answer is the one
            // remaining way this command can mislead, and it cannot be told apart on the
            // wire from a group the caller is not in.
            var scope = await Scope.ReadAsync(context, groupId, page.Items.Count == 0, ct);

            context.Output.Write(page, value =>
            {
                if (value.Items.Count == 0)
                {
                    return new Markup(scope.NothingFound + "\n");
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

                // Which set the rows are of, above them. A page of a group's ledger and a
                // page of your own spending are the same columns in the same table, and
                // until now the only thing that said which was the flag the reader typed.
                return new Rows(new Markup(scope.Heading + "\n"), table, Tables.PageFooter(value));
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
                // Where it was spent, for one filed from a bank. A terminal cannot draw the
                // logo the web app badges onto the row, but it has the name the logo stands
                // for -- and "-" for the expenses nobody imported.
                table.AddRow("Where", Markup.Escape(value.MerchantName ?? "-"));

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
        var merchant = new Option<Guid?>("--merchant-id")
        {
            Description = "Where it was spent, from `groupsplit merchants list`."
        };
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
            name, amount, group, date, description, category, merchant, paidBy, preview
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
                MerchantId = parse.GetValue(merchant),
                PaidByUserId = parse.GetValue(paidBy),
                Description = parse.GetValue(description)
            };

            if (parse.GetValue(preview))
            {
                var split = await context.Transactions.PreviewTransactionSplitsAsync(request, ct);

                context.Output.Write(split, value => RenderSplits(value, "created"));

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
    /// An edit, sent as a JSON Patch of only what changed.
    /// </summary>
    /// <remarks>
    /// Only what changed, because the endpoint reads the patch as well as applying it:
    /// silence about the shares means "keep the ones it has", so an edit that names no
    /// division moves no money. That is what 47c6904 settled, after a pass that read the
    /// silence the other way re-divided 733 expenses and moved 1,394.72 onto one member.
    /// Asking for the division to be worked out again is <c>--redivide</c>, which sends the
    /// one operation that says so -- <c>replace /splits null</c> -- and <c>--split</c> is
    /// how you state the shares yourself. Sending every field every time would put a
    /// division in every patch, so a field nobody moved contributes no operation at all.
    /// <para>
    /// Built by reading the expense, applying the flags to it and comparing -- which is
    /// what the app's edit dialog does, and is the reason it is done that way here. A
    /// <c>--preview</c> needs a whole request rather than a patch, and the alternative was
    /// two readings of what <c>--no-category</c> and friends mean, sitting side by side and
    /// free to drift. One reading, two outputs.
    /// </para>
    /// <para>
    /// It costs a read on every update, which is the price of the single reading. The guard
    /// below still refuses on "no flag named" rather than on "no operation produced", so
    /// setting a field to the value it already holds stays a success and not an error --
    /// which is what a script re-running the same command is doing.
    /// </para>
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
        var merchant = new Option<Guid?>("--merchant-id")
        {
            Description = "Say where it was spent, from `groupsplit merchants list`."
        };
        var noMerchant = new Option<bool>("--no-merchant")
        {
            Description = "Forget where it was spent, so it shows no logo."
        };
        var paidBy = new Option<Guid?>("--paid-by") { Description = "Change who paid." };
        var splits = new Option<string[]>("--split")
        {
            Description = "Set the exact shares as <user-id>=<amount>, repeatable. "
                          + "Without this the existing division is kept.",
            AllowMultipleArgumentsPerToken = true
        };
        var redivide = new Option<bool>("--redivide")
        {
            Description = "Discard the shares it holds and divide it again by its category's rule. "
                          + "Uses the version that divided the expense, where it still records "
                          + "one, rather than the rule as it reads today."
        };
        var preview = new Option<bool>("--preview")
        {
            Description = "Show the split the server would apply without changing anything."
        };
        var handSplit = new Option<bool>("--hand-split")
        {
            Description = "Record that the shares it holds are its own, so no rule works them out again."
        };
        var dividedBy = new Option<Guid?>("--divided-by")
        {
            Description = "Record which version of a rule divided it, from `groupsplit split-rules versions <rule-id>`."
        };

        var command = new Command("update", "Change an expense. Only what you name is sent.")
        {
            TransactionId, name, amount, date, description,
            group, personal, category, noCategory, merchant, noMerchant, paidBy, splits, redivide,
            handSplit, dividedBy, preview
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(TransactionId);

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

            if (parse.GetResult(merchant) is not null && parse.GetValue(noMerchant))
            {
                throw CliException.Input(
                    "--merchant-id and --no-merchant contradict each other.",
                    "Pass one or the other.");
            }

            if (parse.GetValue(splits) is { Length: > 0 } && parse.GetValue(redivide))
            {
                throw CliException.Input(
                    "--split and --redivide contradict each other.",
                    "Pass --split to state the shares yourself, or --redivide to let the category decide.");
            }

            if (parse.GetValue(handSplit) && parse.GetResult(dividedBy) is not null)
            {
                throw CliException.Input(
                    "--hand-split and --divided-by contradict each other.",
                    "Pass --hand-split to say the shares are the expense's own, or --divided-by "
                    + "to name the version that worked them out.");
            }

            // The two pairs below say opposite things about the same fact. --split and
            // --redivide decide what the shares become; these two record what produced the
            // shares it already has and move nothing. Naming one of each in a single command
            // would be saying a division was a rule's in the same breath as typing it out.
            var recordsTheSource = parse.GetValue(handSplit) || parse.GetResult(dividedBy) is not null;

            if (recordsTheSource && parse.GetValue(splits) is { Length: > 0 })
            {
                throw CliException.Input(
                    "--split contradicts --hand-split and --divided-by.",
                    "--split sets the shares; the other two only record what worked out the "
                    + "shares it already has. Run them as two commands if you mean both.");
            }

            if (recordsTheSource && parse.GetValue(redivide))
            {
                throw CliException.Input(
                    "--redivide contradicts --hand-split and --divided-by.",
                    "--redivide works the shares out again; the other two only record what "
                    + "worked out the shares it already has. Run them as two commands if you mean both.");
            }

            // Named, not changed. A flag set to the value the expense already holds
            // produces no operation, and refusing that as "nothing to change" would fail a
            // script running the same command twice.
            var named =
                parse.GetResult(name) is not null ||
                parse.GetResult(amount) is not null ||
                parse.GetResult(date) is not null ||
                parse.GetResult(description) is not null ||
                parse.GetResult(group) is not null ||
                parse.GetResult(category) is not null ||
                parse.GetResult(merchant) is not null ||
                parse.GetResult(paidBy) is not null ||
                parse.GetValue(personal) ||
                parse.GetValue(noCategory) ||
                parse.GetValue(noMerchant) ||
                parse.GetValue(splits) is { Length: > 0 } ||
                parse.GetValue(redivide) ||
                recordsTheSource ||

                // On its own, because "how would this divide if I saved it as it stands"
                // is a real question and the only place the answer is available: a save
                // re-divides, and this is what it would come to.
                parse.GetValue(preview);

            if (!named)
            {
                throw CliException.Input(
                    "Nothing to change.",
                    "Name at least one field, e.g. --name or --amount. "
                    + "See: groupsplit transactions update --help");
            }

            var current = await context.Transactions.GetTransactionAsync(id, ct);

            if (parse.GetValue(preview) && current.Kind is ActivityKind.Transfer)
            {
                throw CliException.Input(
                    "A settlement is not divided, so there is nothing to preview.",
                    "Drop --preview to make the change, or see: groupsplit settlements --help");
            }

            var edited = Edited(parse);

            if (parse.GetValue(preview))
            {
                // Asked exactly when the save would ask it, which is what makes the
                // preview a preview: --redivide sends `replace /splits null`, and
                // `?redivide=true` is the only way to tell the preview endpoint the same
                // thing -- it takes a whole expense, so a body with no shares is
                // indistinguishable from a save that keeps them.
                //
                // Absent rather than false when nobody asked. The endpoint defaults the
                // flag to false, so the two are the same answer, and the absence is the
                // one that reads as silence on the wire.
                var split = await context.Transactions.PreviewUpdatedTransactionSplitsAsync(
                    id, edited, parse.GetValue(redivide) ? true : null, ct);

                context.Output.Write(split, value => RenderSplits(value, "changed"));

                return ExitCodes.Success;
            }

            var patch = Patch(current, edited, parse.GetValue(redivide));
            var updated = await context.Transactions.UpdateTransactionAsync(id, patch, ct);

            // After the edit, and as its own request, because it is a different kind of
            // change: the patch decides what the shares become, and this records what
            // produced the ones it ends up with. Putting it in the patch body would give the
            // save contract a second field about the division, which is the shape that
            // re-divided 733 expenses.
            if (recordsTheSource)
            {
                try
                {
                    updated = await context.Transactions.SetTransactionDivisionSourceAsync(
                        id, new SetDivisionSourceRequest(parse.GetValue(dividedBy)), ct);
                }
                // Both kinds, because the failure can be raised on either side of the
                // mapper: the generated client throws ApiException and only the top-level
                // handler turns one into the envelope, which is too late to know an edit
                // came first.
                catch (Exception failure) when (failure is ApiException or CliException)
                {
                    // Two requests, so there is a state between them, and the exit code alone
                    // cannot say which side of it the command stopped on. Reported as the
                    // second call's own failure it reads as "nothing happened", which would
                    // send somebody looking for an edit that is already saved. Naming the
                    // half that landed is the difference between that and re-running, which
                    // is safe: Patch diffs against a freshly read expense, so the fields that
                    // already moved produce no operation the second time.
                    var mapped = failure as CliException
                                 ?? ApiErrorMapper.Map((ApiException)failure);

                    throw new CliException(
                        mapped.Error with
                        {
                            Message = patch.Operations.Count > 0
                                ? "The edit was saved, but recording what divided it was not: "
                                  + mapped.Error.Message
                                : "There was nothing to edit, and recording what divided it "
                                  + "failed: " + mapped.Error.Message,
                            Remediation = string.Join(' ',
                                new[]
                                {
                                    mapped.Error.Remediation,
                                    "Then run the same command again: it reads the expense first "
                                    + "and sends only what still differs, so nothing is applied twice."
                                }.Where(sentence => !string.IsNullOrWhiteSpace(sentence)))
                        },
                        mapped.ExitCode,
                        failure);
                }
            }

            context.Output.Write(updated, value => new Markup(
                $"[green]Updated[/] {Markup.Escape(value.Name)} ({value.Amount:N2}).\n"));

            return ExitCodes.Success;

            // The one place the flags are read. Everything below works off what it returns,
            // so the preview and the save cannot disagree about what --no-category meant.
            UpdateTransactionRequest Edited(System.CommandLine.ParseResult from) => new()
            {
                Name = from.GetValue(name) ?? current.Name,
                Amount = from.GetValue(amount) ?? current.Amount,
                DateTime = from.GetValue(date) ?? current.DateTime,

                // Read through GetResult, not the value: --description "" is a caller
                // clearing the note, and it arrives indistinguishable from absent otherwise.
                Description = from.GetResult(description) is not null
                    ? from.GetValue(description)
                    : current.Description,

                GroupId = from.GetValue(personal) ? null : from.GetValue(group) ?? current.GroupId,
                CategoryId = from.GetValue(noCategory) ? null : from.GetValue(category) ?? current.CategoryId,
                MerchantId = from.GetValue(noMerchant) ? null : from.GetValue(merchant) ?? current.MerchantId,
                PaidByUserId = from.GetValue(paidBy) ?? current.PaidByUserId,

                // Null unless somebody stated them. Null alone is not "divide it again" --
                // the endpoint keeps the stored shares when a patch says nothing about them
                // -- which is what --redivide is for, and it is carried separately rather than
                // an absence: the endpoint takes silence about the shares as "keep the ones
                // it has", so every other edit here leaves the division exactly as it was.
                // It meant "divide it again" until 47c6904, and a bulk edit that believed
                // that moved 1,394.72 onto one member -- so an edit to the amount alone is
                // refused rather than silently re-divided, and --split is how you say what
                // it becomes.
                Splits = from.GetValue(splits) is { Length: > 0 } given
                    ? Pairs.Splits("--split", given)
                    : null
            };
        });

        return command;
    }

    /// <summary>
    /// The difference between the expense as it stands and as it is to become, as the
    /// operations that carry it.
    /// </summary>
    /// <remarks>
    /// The shares are the member that is not a comparison. <paramref name="edited"/> holds
    /// them only when somebody stated them, and stating them always sends them -- there is
    /// nothing to compare against, because the endpoint reads the absence of the operation
    /// rather than the value.
    /// </remarks>
    private static JsonPatchDocument<UpdateTransactionRequest> Patch(
        TransactionResponse current, UpdateTransactionRequest edited, bool redivide)
    {
        var patch = new JsonPatchDocument<UpdateTransactionRequest>();

        if (current.Name != edited.Name) patch.Replace(request => request.Name, edited.Name);
        if (current.Amount != edited.Amount) patch.Replace(request => request.Amount, edited.Amount);
        if (current.DateTime != edited.DateTime) patch.Replace(request => request.DateTime, edited.DateTime);

        if (current.Description != edited.Description)
            patch.Replace(request => request.Description, edited.Description);

        if (current.GroupId != edited.GroupId) patch.Replace(request => request.GroupId, edited.GroupId);

        if (current.CategoryId != edited.CategoryId)
            patch.Replace(request => request.CategoryId, edited.CategoryId);

        if (current.MerchantId != edited.MerchantId)
            patch.Replace(request => request.MerchantId, edited.MerchantId);

        if (current.PaidByUserId != edited.PaidByUserId)
            patch.Replace(request => request.PaidByUserId, edited.PaidByUserId);

        // Three answers, not two. Shares stated means those amounts; --redivide means an
        // explicit null, which is the only way to ask the endpoint to work them out again;
        // and saying nothing means the expense keeps the shares it has.
        if (edited.Splits is not null)
            patch.Replace(request => request.Splits, edited.Splits);
        else if (redivide)
            patch.Replace(request => request.Splits, null);

        return patch;
    }

    /// <summary>
    /// A division the server worked out, and what worked it out.
    /// </summary>
    /// <param name="nothing">
    /// What the preview did not do -- "created" or "changed". The same shape of answer
    /// comes back from both commands and the reassurance has to name the one they ran.
    /// </param>
    /// <remarks>
    /// The rule line carries a second clause when the version that divided it has been
    /// superseded, which is the common case previewing an edit: the expense is divided
    /// again by the version it was written under, so the numbers will not match the rule as
    /// it reads today and somebody comparing the two deserves to be told why rather than
    /// left to find the discrepancy.
    /// </remarks>
    private static IRenderable RenderSplits(SplitPreviewResponse preview, string nothing)
    {
        var table = Tables.Grid("Member", "Share");

        foreach (var split in preview.Splits)
        {
            table.AddRow(Markup.Escape(split.UserName), split.Amount.ToString("N2"));
        }

        var rule = $"Rule: [bold]{Markup.Escape(preview.RuleName ?? "default")}[/]";

        if (preview.RuleSupersededAt is { } superseded)
        {
            rule += $" [grey](as it stood until {superseded.ToLocalTime():d MMM yyyy}; "
                    + "the rule has changed since)[/]";
        }

        return new Rows(
            new Markup($"[grey]Preview only. Nothing was {nothing}.[/] {rule}\n"),
            table);
    }

    /// <summary>
    /// The total over every match, and -- with <c>--group</c> -- over the group's whole
    /// ledger rather than the caller's part of it.
    /// </summary>
    /// <remarks>
    /// Fixed together with <see cref="List"/> and not after it. The two narrow
    /// independently, so a listing reading the group and a total still reading the payer
    /// would put a page and the figure under it on one screen describing different sets --
    /// which is worse than either being wrong alone, because the two agreeing is what a
    /// reader checks.
    /// </remarks>
    private static Command Summary()
    {
        var group = GroupOption(GroupIsTheLedger);

        var command = new Command("summary",
            "Total the expenses you paid for. With --group, the group's whole ledger.")
        {
            group, PaidBy, From, To, Search, Category
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var groupId = parse.GetValue(group);
            var paidBy = parse.GetValue(PaidBy);

            var summary = groupId is { } id
                ? await context.Groups.GetGroupTransactionsSummaryAsync(
                    id: id,
                    from: parse.GetValue(From),
                    to: parse.GetValue(To),
                    groupId: null,
                    paidByUserId: paidBy,
                    category: parse.GetValue(Category),
                    personal: null,
                    search: parse.GetValue(Search),
                    cancellationToken: ct)
                : await context.Transactions.GetTransactionsSummaryAsync(
                    from: parse.GetValue(From),
                    to: parse.GetValue(To),
                    groupId: null,
                    paidByUserId: paidBy,
                    category: parse.GetValue(Category),
                    personal: null,
                    search: parse.GetValue(Search),
                    cancellationToken: ct);

            var scope = await Scope.ReadAsync(context, groupId, summary.Count == 0, ct);

            context.Output.Write(summary, value => new Rows(
                new Markup(
                    $"[bold]{value.Count}[/] expenses totalling [bold]{value.Total:N2}[/]\n"),
                new Markup(scope.Heading + "\n"),
                // Said every time rather than only when it matters, because a reader cannot
                // tell those times apart: a group that has settled up in full looks exactly
                // like one that never transferred a penny, once the transfers are missing.
                new Markup(Scope.SettlementsExcluded + "\n")));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Which set an answer is about, in the words the answer prints.
    /// </summary>
    /// <remarks>
    /// <see cref="List"/> and <see cref="Summary"/> each answer two questions with one
    /// table, and nothing in the output used to say which -- the reader had to remember
    /// what they typed. Worse, an empty group answer has two meanings the wire cannot tell
    /// apart: the group holds nothing, or the caller is not in it. The group sub-listings
    /// answer a non-member with an empty page rather than a 404, deliberately, so this asks
    /// <c>GET /groups/{id}</c> -- which is membership-scoped and does 404 -- and only ever
    /// on an empty answer, where one extra request buys the one distinction that matters.
    /// </remarks>
    private readonly record struct Scope(string Heading, string NothingFound)
    {
        /// <summary>
        /// The caveat the totals carry. A settlement is a transfer rather than an expense,
        /// so none of these figures has been paid back -- which is exactly what somebody
        /// totalling a group's spend is liable to assume they have.
        /// </summary>
        public const string SettlementsExcluded =
            "[grey]Expenses only -- a settlement is a transfer, so nothing here has been paid "
            + "back. Everything a group did: [/]groupsplit groups activity <group-id>";

        private const string YoursHeading = "[grey]Expenses you paid for, across every group.[/]";

        private const string YoursEmpty =
            "[grey]No expenses you paid for. This listing is yours alone -- for a group's whole "
            + "ledger, whoever paid, add [/]--group <group-id>";

        public static async Task<Scope> ReadAsync(
            CliContext context, Guid? groupId, bool empty, CancellationToken ct)
        {
            if (groupId is not { } id)
                return new Scope(YoursHeading, YoursEmpty);

            // Nothing to disambiguate: rows came back, so the caller is plainly a member.
            if (!empty)
                return new Scope("[grey]This group's whole ledger, whoever paid.[/]", string.Empty);

            var name = Markup.Escape(await NameOrRefuseAsync(context, id, ct));

            return new Scope(
                $"[grey]{name}: the whole ledger, whoever paid.[/]",
                $"[grey]Nothing in {name} matches. Settlements are not expenses; for those "
                + $"see [/]groupsplit groups activity {id}");
        }

        /// <summary>
        /// The group's name, or a refusal saying why the answer was empty.
        /// </summary>
        /// <exception cref="CliException">
        /// The caller is not in the group. Exit code 3 rather than 1, because no retry
        /// helps: the id is either somebody else's group or one this account has left.
        /// </exception>
        private static async Task<string> NameOrRefuseAsync(
            CliContext context, Guid id, CancellationToken ct)
        {
            try
            {
                return (await context.Groups.GetGroupAsync(id, ct)).Name;
            }
            catch (ApiException api) when (api.StatusCode == (int)HttpStatusCode.NotFound)
            {
                throw new CliException(
                    new CliError(
                        "You are not in that group, so it has no ledger you can read. That is "
                        + "why the answer was empty rather than a refusal.",
                        Shared.Errors.ErrorCodes.GroupNotFound,
                        "List the groups you are in with: groupsplit groups list"),
                    ExitCodes.InvalidInput);
            }
        }
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
    /// <summary>
    /// Keeps only the rows that are actually a debt: a share of something somebody else paid
    /// for. Your share of an expense you paid for yourself is money you already have.
    /// </summary>
    private static readonly Option<bool> OwedOnly = new("--owed-only")
    {
        Description = "Only the shares on expenses somebody else paid."
    };

    /// <summary>
    /// What you paid and what your share came to, month by month.
    /// </summary>
    /// <remarks>
    /// Two figures, and only the two the server answers. There used to be a third -- paid
    /// minus share, headed "Fronted" -- and it was a claim the data cannot support: a
    /// settlement is a transfer rather than an expense, so none of it is here and the gap
    /// takes no account of what has already been paid back. Somebody who had settled up in
    /// full still read a month of being owed hundreds. The web client drew the same gap as
    /// a chart captioned "the gap is how much you are fronting", and that is gone for the
    /// same reason. Where somebody actually stands is <c>groupsplit users position</c>,
    /// which reads the balances and does count transfers.
    /// </remarks>
    private static Command Monthly()
    {
        var group = GroupOption(GroupNarrowsYours);

        var command = new Command("monthly", "What you paid and what your share came to, by month.")
        {
            group, From, To, Search, Category
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var months = await context.Transactions.GetMonthlyExposureAsync(
                from: parse.GetValue(From),
                to: parse.GetValue(To),
                groupId: parse.GetValue(group),
                paidByUserId: null,
                category: parse.GetValue(Category),
                personal: null,
                search: parse.GetValue(Search),
                cancellationToken: ct);

            context.Output.Write(months, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("months") + "\n");
                }

                var table = Tables.Grid("Month", "Paid", "Your share");

                foreach (var month in value)
                {
                    table.AddRow(
                        month.Month.ToString("yyyy-MM"),
                        month.Paid.ToString("N2"),
                        month.Share.ToString("N2"));
                }

                // Under the table rather than only in the description, because the
                // description is read once and the figures are read every time.
                return new Rows(table, new Markup(
                    "[grey]Yours alone, and gross: a settlement is a transfer, so nothing here "
                    + "has been paid back. Where you stand: [/]groupsplit users position\n"));
            });

            return ExitCodes.Success;
        });

        return command;
    }

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

        var group = GroupOption(GroupNarrowsYours);

        var command = new Command("list", "List the expenses you owe a share of, newest first.")
        {
            group, From, To, Search, Category, OwedOnly, shareSortBy, Order, Page, PageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var page = await context.Transactions.GetTransactionSharesAsync(
                from: parse.GetValue(From),
                to: parse.GetValue(To),
                groupId: parse.GetValue(group),
                paidByUserId: null,
                category: parse.GetValue(Category),
                personal: null,
                search: parse.GetValue(Search),
                sortBy: parse.GetValue(shareSortBy),
                sortDescending: parse.GetValue(Order).Descending(),
                page: parse.GetValue(Page),
                pageSize: parse.GetValue(PageSize),
                owedOnly: parse.GetValue(OwedOnly) ? true : null,
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
        var group = GroupOption(GroupNarrowsYours);

        var command = new Command("summary", "Total the shares matching a filter.")
        {
            group, From, To, Search, Category, OwedOnly
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var summary = await context.Transactions.GetTransactionSharesSummaryAsync(
                from: parse.GetValue(From),
                to: parse.GetValue(To),
                groupId: parse.GetValue(group),
                paidByUserId: null,
                category: parse.GetValue(Category),
                personal: null,
                search: parse.GetValue(Search),
                owedOnly: parse.GetValue(OwedOnly) ? true : null,
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

    /// <summary>
    /// Points a group's expenses at the version of their rule that was in force on the day
    /// each was spent.
    /// </summary>
    /// <remarks>
    /// The second half of writing a rule's history, and useless without it: the history says
    /// what the rule stood for and when, and this says which of those an expense actually
    /// fell under. A migration out of a workbook pointed every categorised expense at the
    /// only version there was, so a 2023 grocery bill claims a ratio agreed in 2026.
    /// <para>
    /// It moves no money. Not one share is read, let alone written -- the only thing that
    /// changes is which version each expense names -- so the group's balances are the same
    /// afterwards to the cent. It stops for a confirmation anyway, because it rewrites the
    /// history of an entire ledger and <c>--dry-run</c> is right there.
    /// </para>
    /// </remarks>
    private static Command Reattach()
    {
        var group = new Option<Guid>("--group")
        {
            Description = "The group whose expenses to re-point.",
            Required = true
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Work out what would change and report it without saving anything."
        };

        var command = new Command(
            "reattach",
            "Point a group's expenses at the version of their rule in force when each was spent.")
        {
            group, dryRun
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var groupId = parse.GetValue(group);
            var dry = parse.GetValue(dryRun);

            if (!dry)
            {
                Confirmation.Require(
                    context,
                    action: "transactions.reattach",
                    summary: $"Re-point every expense in group {groupId} at the rule version of its own date?",
                    changes:
                    [
                        "Each expense filed under a category with a rule points at the version "
                        + "that was in force on the day it was spent.",
                        "An expense older than its rule's history is left pointing at nothing.",
                        "No share and no balance changes: only which version each expense names.",
                        $"See it first with: groupsplit transactions reattach --group {groupId} --dry-run"
                    ],
                    confirmCommand: $"groupsplit transactions reattach --group {groupId} --yes");
            }

            var summary = await context.Transactions.ReattachTransactionsAsync(
                new ReattachTransactionsRequest { GroupId = groupId, DryRun = dry }, ct);

            context.Output.Write(summary, value =>
            {
                var heading = value.DryRun
                    ? "[grey]Dry run. Nothing was saved.[/]"
                    : "[green]Reattached.[/]";

                var totals = new Markup(
                    $"{heading} [bold]{value.Examined}[/] expenses examined, "
                    + $"[bold]{value.Changed}[/] re-pointed, "
                    + $"[bold]{value.LeftWithoutAVersion}[/] left with no version.\n"
                    + "[grey]No share and no balance changed.[/]\n");

                if (value.ByRule.Count == 0)
                {
                    return new Rows(totals, new Markup(
                        Tables.Empty("expenses filed under a category with a rule") + "\n"));
                }

                var table = Tables.Grid("Rule", "Examined", "Re-pointed", "No version");

                foreach (var rule in value.ByRule)
                {
                    table.AddRow(
                        Markup.Escape(rule.SplitRuleName),
                        rule.Examined.ToString(),
                        rule.Changed.ToString(),
                        rule.Uncovered.ToString());
                }

                return new Rows(totals, table);
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Deletes either kind of transaction: an expense, or a settlement between two members.
    /// </summary>
    /// <remarks>
    /// The id of a settlement comes from <c>groupsplit groups activity</c> -- the one
    /// listing that shows them. It will not appear in <c>transactions list</c>, which reads
    /// the expenses, and neither will <c>transactions show</c> describe it; the DELETE
    /// takes it all the same, which is why the lookup below is allowed to come back empty
    /// instead of ending the command.
    /// </remarks>
    private static Command Delete()
    {
        var command = new Command("delete", "Delete a transaction: an expense, or a settlement.")
        {
            TransactionId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(TransactionId);
            var transaction = await TryShow(context, id, ct);

            // Named when it can be. A 404 from the lookup does not say "no such row" here
            // -- a settlement answers that way too -- so the prompt claims only the id, and
            // a genuinely absent one is refused by the DELETE a moment later.
            Confirmation.Require(
                context,
                action: "transactions.delete",
                summary: transaction is null
                    ? $"Delete transaction {id}?"
                    : $"Delete '{transaction.Name}'?",
                changes:
                [
                    transaction is null
                        ? "The transaction is removed. If it is a settlement, the repayment it "
                          + "recorded is undone and the debt it cleared comes back."
                        : $"'{transaction.Name}' for {transaction.Amount:N2} is removed.",
                    "Every member's balance in the group is recalculated."
                ],
                confirmCommand: $"groupsplit transactions delete {id} --yes");

            await context.Transactions.DeleteTransactionAsync(id, ct);

            context.Output.Write(
                new { status = "deleted", transactionId = id, name = transaction?.Name },
                value => new Markup(
                    $"[green]Deleted[/] {Markup.Escape(value.name ?? value.transactionId.ToString())}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The transaction as <c>transactions show</c> would report it, or null when that
    /// listing cannot see it -- a settlement, or an id belonging to nobody the caller knows.
    /// </summary>
    /// <remarks>
    /// Only 404 is swallowed. Anything else -- unauthorised, a server fault, an unreachable
    /// host -- is the command's problem and is left to the handler that reports it, so a
    /// broken connection cannot read as "just a settlement then" and go on to a delete.
    /// </remarks>
    private static async Task<TransactionResponse?> TryShow(CliContext context, Guid id,
        CancellationToken ct)
    {
        try
        {
            return await context.Transactions.GetTransactionAsync(id, ct);
        }
        catch (ApiException api) when (api.StatusCode is (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
