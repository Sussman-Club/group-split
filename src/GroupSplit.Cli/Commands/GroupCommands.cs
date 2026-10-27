using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

public static class GroupCommands
{
    private static readonly Argument<Guid> GroupId = new("group-id")
    {
        Description = "The group's id, as shown by `groupsplit groups list`."
    };

    public static Command Build()
    {
        var groups = new Command("groups", "Groups you belong to, their members and their balances.");

        groups.Subcommands.Add(List());
        groups.Subcommands.Add(Show());
        groups.Subcommands.Add(Create());
        groups.Subcommands.Add(Rename());
        groups.Subcommands.Add(SetDefaultSplit());
        groups.Subcommands.Add(SetDefaultPeriod());
        groups.Subcommands.Add(Members());
        groups.Subcommands.Add(RemoveMember());
        groups.Subcommands.Add(Balances());
        groups.Subcommands.Add(Settle());
        groups.Subcommands.Add(SettleBetween());
        groups.Subcommands.Add(SettleUp());
        groups.Subcommands.Add(Activity());
        groups.Subcommands.Add(Spending());
        groups.Subcommands.Add(Archive());
        groups.Subcommands.Add(Unarchive());
        groups.Subcommands.Add(Leave());
        groups.Subcommands.Add(AddGuest());
        groups.Subcommands.Add(Guests());
        groups.Subcommands.Add(GuestLink());
        groups.Subcommands.Add(Invite());
        groups.Subcommands.Add(Invitations());
        groups.Subcommands.Add(Withdraw());
        groups.Subcommands.Add(JoinLinkCommands.Build());

        return groups;
    }

    private static Command List()
    {
        var command = new Command("list", "List every group you are a member of.");

        command.SetHandler(async (context, ct) =>
        {
            var groups = await context.Groups.GetGroupsAsync(cancellationToken: ct);

            context.Output.Write(groups, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("groups") + "\n");
                }

                var table = Tables.Grid("Id", "Name", "Members", "State");

                foreach (var group in value)
                {
                    table.AddRow(
                        group.Id.ToString(),
                        Markup.Escape(group.Name),
                        group.MemberCount.ToString(),
                        group.IsPersonal ? "personal" : group.IsArchive ? "[grey]archived[/]" : "active");
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Show()
    {
        var command = new Command("show", "Show one group.") { GroupId };

        command.SetHandler(async (context, ct) =>
        {
            var group = await context.Groups.GetGroupAsync(context.ParseResult.GetValue(GroupId), ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Create()
    {
        var name = new Argument<string>("name") { Description = "Name for the new group." };
        var description = new Option<string?>("--description")
        {
            Description = "Optional context for the group, such as which trip it is."
        };
        var command = new Command("create", "Create a group.") { name, description };

        command.SetHandler(async (context, ct) =>
        {
            var group = await context.Groups.CreateGroupAsync(
                new CreateGroupRequest
                {
                    Name = context.ParseResult.GetValue(name)!,
                    Description = context.ParseResult.GetValue(description) ?? string.Empty
                }, ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// A JSON Patch of one operation, which is what the endpoint takes. The name is the
    /// only thing a group has to change, so it is a command rather than a general
    /// <c>update</c> with a patch document to hand-write.
    /// </summary>
    private static Command Rename()
    {
        var name = new Argument<string>("name") { Description = "The group's new name." };
        var description = new Option<string?>("--description")
        {
            Description = "Replace the optional group description. Pass an empty value to clear it."
        };
        var command = new Command("rename", "Rename a group.") { GroupId, name, description };

        command.SetHandler(async (context, ct) =>
        {
            var patch = new JsonPatchDocument<CreateGroupRequest>();
            patch.Replace(request => request.Name, context.ParseResult.GetValue(name)!);

            if (context.ParseResult.GetResult(description) is not null)
                patch.Replace(request => request.Description, context.ParseResult.GetValue(description) ?? string.Empty);

            var group = await context.Groups.UpdateGroupAsync(
                context.ParseResult.GetValue(GroupId), patch, ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// What an expense in this group divides by when it names no category, and its category
    /// names no rule.
    /// </summary>
    /// <remarks>
    /// A command of its own rather than a flag on <c>rename</c>, for the reason rename is a
    /// command rather than a patch document to hand-write: it is the second thing a group
    /// holds that anybody changes, and naming it separately keeps either from being sent by
    /// accident alongside the other.
    /// <para>
    /// Every group is given an "Even" rule and starts pointed at it, so this always replaces
    /// a default rather than setting a first one. The one rule it cannot be given is the
    /// group's "divide it by the bill": that rule reads the lines of a receipt, and an
    /// expense with no receipt has nothing for it to read, so the API refuses it with
    /// SPLIT_RULE_INVALID rather than leaving the group unable to record an ordinary expense.
    /// </para>
    /// </remarks>
    private static Command SetDefaultSplit()
    {
        var ruleId = new Argument<Guid>("rule-id")
        {
            Description = "The rule's id, as shown by `groupsplit split-rules list --group <group-id>`."
        };

        var command = new Command("set-default-split",
            "Choose what an uncategorized expense in this group divides by.")
        {
            GroupId, ruleId
        };

        command.SetHandler(async (context, ct) =>
        {
            var patch = new JsonPatchDocument<CreateGroupRequest>();
            patch.Replace(request => request.DefaultSplitRuleId, context.ParseResult.GetValue(ruleId));

            var group = await context.Groups.UpdateGroupAsync(
                context.ParseResult.GetValue(GroupId), patch, ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The span the group's Activity tab opens on in the app, for every member.
    /// </summary>
    /// <remarks>
    /// A command of its own, like <c>set-default-split</c>. It is a display preference for the
    /// app and nothing more: <c>groups activity</c> ignores it and reads only the
    /// <c>--from</c> and <c>--to</c> it is given, so a script always gets what it asked for.
    /// </remarks>
    private static Command SetDefaultPeriod()
    {
        var period = new Argument<ActivityPeriod>("period")
        {
            Description = "all-time, this-month, last-month, last-3-months or this-year.",
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;

                if (Periods.FirstOrDefault(known => string.Equals(known.Name, token, StringComparison.OrdinalIgnoreCase))
                    is { Name: not null } match)
                    return match.Period;

                result.AddError($"Could not read period '{token}'. Use one of: {string.Join(", ", Periods.Select(known => known.Name))}.");
                return default;
            }
        };
        period.CompletionSources.Add(Periods.Select(known => known.Name).ToArray());

        var command = new Command("set-default-period",
            "Choose which span this group's Activity tab opens on in the app.")
        {
            GroupId, period
        };

        command.SetHandler(async (context, ct) =>
        {
            var patch = new JsonPatchDocument<CreateGroupRequest>();
            patch.Replace(request => request.DefaultPeriod, context.ParseResult.GetValue(period));

            var group = await context.Groups.UpdateGroupAsync(
                context.ParseResult.GetValue(GroupId), patch, ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>The periods as the command line spells them.</summary>
    private static readonly (string Name, ActivityPeriod Period)[] Periods =
    [
        ("all-time", ActivityPeriod.AllTime),
        ("this-month", ActivityPeriod.ThisMonth),
        ("last-month", ActivityPeriod.LastMonth),
        ("last-3-months", ActivityPeriod.LastThreeMonths),
        ("this-year", ActivityPeriod.ThisYear)
    ];

    private static Command Members()
    {
        var command = new Command("members",
            "List a group's people: its members, and its guests.")
        {
            GroupId
        };

        command.SetHandler(async (context, ct) =>
        {
            var members = await context.Groups.GetGroupMembersAsync(context.ParseResult.GetValue(GroupId), ct);

            context.Output.Write(members, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("members") + "\n");
                }

                // The status column is not decoration. This listing is what an agent reads
                // to find the id to put in a split, name as the payer or settle with, and a
                // guest is all three -- while being nobody who can see the group.
                var table = Tables.Grid("Id", "Name", "Email", "Status");

                foreach (var member in value)
                {
                    table.AddRow(
                        member.Id.ToString(),
                        Markup.Escape(member.FullName),
                        Markup.Escape(member.Email ?? "-"),
                        member.IsGuest ? "[yellow]guest[/]" : "member");
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Removing somebody else -- a member or a guest -- which is not the same command as
    /// leaving: only somebody with a settled balance can go, and the API refuses the rest.
    /// </summary>
    private static Command RemoveMember()
    {
        var userId = new Argument<Guid>("user-id")
        {
            Description = "The member's or guest's id, as shown by `groupsplit groups members`."
        };

        var command = new Command("remove-member", "Remove a member or a guest from a group.")
        {
            GroupId, userId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var member = context.ParseResult.GetValue(userId);

            // Read the roster first so the confirmation can name the person. A guid is not
            // something anybody should have to recognise before agreeing to remove it.
            var members = await context.Groups.GetGroupMembersAsync(id, ct);

            // The same read says when there is nobody to remove, and saying it here spares
            // the caller a confirmation for something the server would only refuse: somebody
            // who has left, or never belonged, is not on the roster at all.
            var removed = members.FirstOrDefault(candidate => candidate.Id == member)
                          ?? throw new CliException(
                              new CliError(
                                  "That person is not a member or guest of this group.",
                                  Shared.Errors.ErrorCodes.UserNotFound,
                                  $"List who is: groupsplit groups members {id}"),
                              ExitCodes.InvalidInput);

            var name = removed.FullName;

            // A guest goes on the same terms -- square first, kept in the history -- but has
            // no access to lose and never could read the join link, so that one stays.
            string[] changes = removed.IsGuest
                ?
                [
                    $"{name} stops being a guest: nobody can give them a share or settle with them.",
                    $"{name} stays in the group's history as a past guest.",
                    "Their link, if they have one, stops working.",
                    "Refused unless they are square with the group."
                ]
                :
                [
                    $"{name} loses access to the group and its history.",
                    "The group's join link stops working; make a new one to share.",
                    $"{name} needs a link issued after this to come back.",
                    "Refused if their balance in it is not settled."
                ];

            Confirmation.Require(
                context,
                action: "groups.remove-member",
                summary: $"Remove {name} from the group?",
                changes: changes);

            var group = await context.Groups.RemoveGroupMemberAsync(id, member, ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Balances()
    {
        var through = ThroughOption.Create();

        var command = new Command("balances", "Show who owes whom in a group.") { GroupId, through };

        command.SetHandler(async (context, ct) =>
        {
            var day = context.ParseResult.GetValue(through);

            var balances = await context.Groups.GetGroupUserBalanceAsync(
                context.ParseResult.GetValue(GroupId), day, ThroughOption.OffsetFor(day), ct);

            context.Output.Write(balances, value =>
            {
                var table = Tables.Grid("Member", "Paid", "Owed", "Balance");

                foreach (var balance in value.NetBalances)
                {
                    // A guest is in this column and in the transfers below like anybody else;
                    // the marker says the members record their side of it.
                    table.AddRow(
                        Markup.Escape(balance.UserName) +
                        (balance.IsGuest ? " [yellow](guest)[/]" : string.Empty),
                        balance.AmountPaid.ToString("N2"),
                        balance.AmountOwed.ToString("N2"),
                        Tables.Money(balance.Balance));
                }

                // The minimised transfers, which are the part somebody acts on: a column of
                // net balances says how it stands, and these say what to pay.
                return new Rows(
                    table,
                    Debts("Owed to you", value.OwedToYou),
                    Debts("You owe", value.YouOwed));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static IRenderable Debts(string heading, IEnumerable<DebtInfo> debts)
    {
        var list = debts.ToList();

        if (list.Count == 0)
        {
            return new Markup(string.Empty);
        }

        var table = Tables.Grid("Member", "Amount");

        foreach (var debt in list)
        {
            table.AddRow(Markup.Escape(debt.UserName), debt.Amount.ToString("N2"));
        }

        return new Rows(new Markup($"\n[bold]{Markup.Escape(heading)}[/]\n"), table);
    }

    /// <summary>
    /// Records one member paying another back. The direction is stated rather than read off
    /// the balance, because a debtor recording their own repayment acts precisely when the
    /// balance still says they owe -- see <see cref="SettlementDirection"/>.
    /// </summary>
    private static Command Settle()
    {
        var userId = new Argument<Guid>("user-id") { Description = "The other member, or a guest." };
        var amount = Amounts.Argument("amount", "How much changed hands, e.g. 40.00.");

        var direction = new Option<SettlementDirection>("--direction")
        {
            Description = "Who paid whom. Defaults to they-paid-you, the creditor's side.",
            DefaultValueFactory = _ => SettlementDirection.TheyPaidYou
        };

        var date = new Option<DateTimeOffset?>("--date")
        {
            Description = "When the money moved, e.g. 2026-09-30. Defaults to now."
        };

        var note = new Option<string?>("--note")
        {
            Description = "What to remember about it, e.g. \"cash\" or a transfer reference."
        };

        // Remembered on the repayment and nothing more: the amount is the one given. Read the
        // balances with the same --through first to know what that period left owing.
        var through = ThroughOption.Create(
            "The last day of activity this repayment is for, e.g. 2026-08-31. Recorded on it; the amount is still yours.");

        var command = new Command("settle", "Record a repayment between you and another member or a guest.")
        {
            GroupId, userId, amount, direction, date, note, through
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(GroupId);
            var other = parse.GetValue(userId);
            var paid = parse.GetValue(amount);
            var side = parse.GetValue(direction);
            var when = parse.GetValue(date);
            var description = parse.GetValue(note);
            var day = parse.GetValue(through);

            var members = await context.Groups.GetGroupMembersAsync(id, ct);
            var name = members.FirstOrDefault(candidate => candidate.Id == other)?.FullName
                       ?? other.ToString();

            Confirmation.Require(
                context,
                action: "groups.settle",
                summary: side == SettlementDirection.TheyPaidYou
                    ? $"Record that {name} paid you {paid:N2}?"
                    : $"Record that you paid {name} {paid:N2}?",
                changes: [$"A transfer of {paid:N2} is written, and both balances move by it."]);

            await context.Groups.SettleGroupDebtsAsync(
                id,
                new SettleRequest
                {
                    UserId = other,
                    Amount = paid,
                    Direction = side,
                    Date = when,
                    Description = description,
                    Through = day
                },
                ct);

            context.Output.Write(
                new { status = "settled", groupId = id, userId = other, amount = paid, direction = side },
                value => new Markup(
                    $"[green]Settled[/] {value.amount:N2} with {Markup.Escape(name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Records one repayment between two named people -- members or guests -- whichever of
    /// them you are, including neither.
    /// </summary>
    /// <remarks>
    /// The way to record two guests paying each other back, which only a member can write
    /// down.
    /// <para>
    /// Separate from <c>settle</c> and <c>settle-up</c> because both of those put you on one
    /// end, and saying something about two other people should have to be asked for by name.
    /// </para>
    /// <para>
    /// What it is for is a ledger the group already agreed on: months closed in a spreadsheet
    /// years ago, moving in. Their repayments have to be written for the imbalance their
    /// expenses carry to ever clear, and whoever is doing the moving is on neither end of
    /// most of them.
    /// </para>
    /// </remarks>
    private static Command SettleBetween()
    {
        var fromUserId = new Argument<Guid>("from-user-id") { Description = "Who paid: a member or a guest." };
        var toUserId = new Argument<Guid>("to-user-id") { Description = "Who was paid: a member or a guest." };

        var amount = Amounts.Argument("amount", "How much changed hands, e.g. 40.00.");

        var date = new Option<DateTimeOffset?>("--date")
        {
            Description = "When the money moved, e.g. 2026-09-30. Defaults to now."
        };

        var note = new Option<string?>("--note")
        {
            Description = "What to remember about it, e.g. \"end of September 2024\"."
        };

        var command = new Command("settle-between",
            "Record a repayment between two people in a group, whoever you are.")
        {
            GroupId, fromUserId, toUserId, amount, date, note
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(GroupId);
            var payer = parse.GetValue(fromUserId);
            var payee = parse.GetValue(toUserId);
            var paid = parse.GetValue(amount);

            var members = await context.Groups.GetGroupMembersAsync(id, ct);

            string NameOf(Guid who) =>
                members.FirstOrDefault(candidate => candidate.Id == who)?.FullName ?? who.ToString();

            var payerName = NameOf(payer);
            var payeeName = NameOf(payee);

            Confirmation.Require(
                context,
                action: "groups.settle-between",
                summary: $"Record that {payerName} paid {payeeName} {paid:N2}?",
                changes:
                [
                    $"A transfer of {paid:N2} is written from {payerName} to {payeeName}.",
                    "Both of their balances move by it, and neither of them recorded it."
                ]);

            var recorded = await context.Groups.RecordRepaymentAsync(
                id,
                new RecordRepaymentRequest
                {
                    FromUserId = payer,
                    ToUserId = payee,
                    Amount = paid,
                    Date = parse.GetValue(date),
                    Description = parse.GetValue(note)
                },
                ct);

            context.Output.Write(
                new
                {
                    status = "settled",
                    groupId = id,
                    fromUserId = payer,
                    toUserId = payee,
                    amount = recorded.Amount
                },
                value => new Markup(
                    $"[green]Recorded[/] {value.amount:N2} from {Markup.Escape(payerName)} "
                    + $"to {Markup.Escape(payeeName)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Squares up everything between you and the rest of the group at once.
    /// </summary>
    /// <remarks>
    /// Your own position, not the group's: every repayment it writes has you on one end,
    /// because what you paid and what you were paid are things you were there for and money
    /// moving between two other members is not yours to record.
    /// <para>
    /// Reads the balances first, so the confirmation lists the repayments it is about to
    /// write rather than describing them.
    /// </para>
    /// </remarks>
    private static Command SettleUp()
    {
        var date = new Option<DateTimeOffset?>("--date")
        {
            Description = "When the money moved, e.g. 2026-09-30. Defaults to now."
        };

        var note = new Option<string?>("--note")
        {
            Description = "What to remember about it, written onto every repayment."
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would be recorded and stop."
        };

        var through = ThroughOption.Create(
            "Square up only what is owed for activity dated up to this day, e.g. 2026-08-31.");

        var command = new Command("settle-up", "Square up with everybody in a group at once.")
        {
            GroupId, date, note, dryRun, through
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(GroupId);
            var day = parse.GetValue(through);
            var offset = ThroughOption.OffsetFor(day);

            var balance = await context.Groups.GetGroupUserBalanceAsync(id, day, offset, ct);

            var payments = new List<(string Line, decimal Amount)>();

            payments.AddRange(balance.YouOwed.Select(debt =>
                ($"You pay {debt.UserName} {debt.Amount:N2}.", debt.Amount)));

            payments.AddRange(balance.OwedToYou.Select(credit =>
                ($"{credit.UserName} pays you {credit.Amount:N2}.", credit.Amount)));

            if (payments.Count == 0)
            {
                context.Output.Write(
                    new { status = "already-square", groupId = id, through = day },
                    _ => new Markup(day is { } squareThrough
                        ? $"You are already square with everybody in this group through {squareThrough:yyyy-MM-dd}.\n"
                        : "You are already square with everybody in this group.\n"));

                return ExitCodes.Success;
            }

            if (parse.GetValue(dryRun))
            {
                context.Output.Write(
                    new { groupId = id, through = day, payments = payments.Select(payment => payment.Line) },
                    _ => new Rows(
                        new Markup($"[bold]{payments.Count}[/] repayments, "
                                   + $"{payments.Sum(payment => payment.Amount):N2} in all.\n"),
                        Lines(payments)));

                return ExitCodes.Success;
            }

            Confirmation.Require(
                context,
                action: "groups.settle-up",
                summary: $"Record {payments.Count} "
                         + $"{(payments.Count == 1 ? "repayment" : "repayments")} and square up?",
                changes: [.. payments.Select(payment => payment.Line)]);

            var settled = await context.Groups.SettleUpAsync(
                id,
                new SettleUpRequest
                {
                    Date = parse.GetValue(date),
                    Description = parse.GetValue(note),
                    Through = day,
                    UtcOffset = offset
                },
                ct);

            context.Output.Write(settled, value => new Markup(
                $"[green]Settled up[/] -- {value.Payments.Count} "
                + $"{(value.Payments.Count == 1 ? "repayment" : "repayments")}, "
                + $"{value.Total:N2} in all.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static IRenderable Lines(IEnumerable<(string Line, decimal Amount)> payments)
    {
        var table = Tables.Grid("Repayment");

        foreach (var payment in payments)
        {
            table.AddRow(Markup.Escape(payment.Line));
        }

        return table;
    }

    /// <summary>
    /// The group's ledger: the one listing that shows transfers as well as expenses, which
    /// is why it is not <c>transactions list --group</c>. "Omar paid you 40" is the row
    /// people look for when a balance moves, and every other expense surface cannot see it.
    /// </summary>
    /// <remarks>
    /// It carries two figures beside the amount: what each entry cost the caller, and where
    /// they stood immediately after it. The balance is why the two tabs this replaces became
    /// one -- a balance history cannot be drawn against a list that hides half the events
    /// that move it -- and it is cumulative over the whole ledger, so narrowing the list
    /// does not rewrite it.
    /// </remarks>
    private static Command Activity()
    {
        var sortBy = new Option<string?>("--sort-by")
        {
            Description = "dateTime, amount or name. Defaults to dateTime."
        };

        var kind = new Option<ActivityKind?>("--kind")
        {
            Description = "Expense or Transfer. Both when it is not said."
        };

        var from = new Option<DateTimeOffset?>("--from") { Description = "Inclusive lower bound on the date." };
        var to = new Option<DateTimeOffset?>("--to") { Description = "Inclusive upper bound on the date." };

        var search = new Option<string?>("--search")
        {
            Description = "Match a name, note, category, or either party's name."
        };
        var categoryId = new Option<Guid?>("--category-id")
        {
            Description = "Only expenses filed under this category."
        };
        var uncategorized = new Option<bool>("--uncategorized")
        {
            Description = "Only expenses with no category."
        };
        var paidBy = new Option<Guid?>("--paid-by")
        {
            Description = "Only activity paid by this member or guest."
        };

        var order = Sorting.Order();
        var page = new Option<int?>("--page") { Description = "1-based page number." };
        var pageSize = new Option<int?>("--page-size")
        {
            Description = "Rows per page. The server caps this."
        };

        var command = new Command("activity", "List everything that happened in a group, newest first.")
        {
            GroupId, kind, from, to, search, categoryId, uncategorized, paidBy, sortBy, order, page, pageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var activity = await context.Groups.GetGroupActivityAsync(
                id: parse.GetValue(GroupId),
                from: parse.GetValue(from),
                to: parse.GetValue(to),
                kind: (int?)parse.GetValue(kind),
                search: parse.GetValue(search),
                categoryId: parse.GetValue(categoryId),
                uncategorized: parse.GetValue(uncategorized) ? true : null,
                paidBy: parse.GetValue(paidBy),
                sortBy: parse.GetValue(sortBy),
                sortDescending: parse.GetValue(order).Descending(),
                page: parse.GetValue(page),
                pageSize: parse.GetValue(pageSize),
                cancellationToken: ct);

            context.Output.Write(activity, value =>
            {
                if (value.Items.Count == 0)
                {
                    return new Markup(Tables.Empty("activity") + "\n");
                }

                // Both parties in one column rather than two. The share and the balance are
                // two new columns and a terminal has a width: eight of them truncated every
                // cell to five characters, which is a table that says nothing.
                var table = Tables.Grid("Date", "Kind", "What", "Amount", "Your share", "Balance");

                foreach (var entry in value.Items)
                {
                    table.AddRow(
                        entry.DateTime.ToLocalTime().ToString("yyyy-MM-dd"),
                        entry.Kind == ActivityKind.Transfer ? "transfer" : "expense",
                        Markup.Escape(Describe(entry)),
                        entry.Amount.ToString("N2"),
                        // A dash rather than a zero: a transfer is not a cost anybody
                        // carries a part of.
                        entry.Share is { } share ? share.ToString("N2") : "[grey]-[/]",
                        Tables.Money(entry.RunningBalance));
                }

                return new Rows(table, Tables.PageFooter(value));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>One row per category, rather than one per expense, for a group's spending.</summary>
    private static Command Spending()
    {
        var from = new Option<DateTimeOffset?>("--from") { Description = "Inclusive lower bound on the date." };
        var to = new Option<DateTimeOffset?>("--to") { Description = "Inclusive upper bound on the date." };
        var paidBy = new Option<Guid?>("--paid-by") { Description = "Only expenses paid by this member or guest." };
        var command = new Command("spending", "Show a group's spending by category.") { GroupId, from, to, paidBy };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var spending = await context.Groups.GetGroupSpendingByCategoryAsync(
                parse.GetValue(GroupId), from: parse.GetValue(from), to: parse.GetValue(to),
                paidBy: parse.GetValue(paidBy), cancellationToken: ct);

            context.Output.Write(spending, value =>
            {
                if (value.Categories.Count == 0)
                    return new Markup(Tables.Empty("spending") + "\n");

                var table = Tables.Grid("Category", "Expenses", "Total", "Your share");
                foreach (var category in value.Categories)
                    table.AddRow(Markup.Escape(category.Name), category.Count.ToString(),
                        category.Total.ToString("N2"), category.Share.ToString("N2"));

                return new Rows(table);
            });
            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// One entry in a phrase: who paid whom on a settlement, and what was bought and by whom
    /// on an expense.
    /// </summary>
    private static string Describe(GroupActivityResponse entry) =>
        entry.Kind == ActivityKind.Transfer
            ? $"{entry.PaidByUserName} paid {entry.PaidToUserName}"
            : $"{entry.Name} ({entry.PaidByUserName})";

    private static Command Archive()
    {
        var command = new Command("archive", "Archive a group, hiding it from the active list.") { GroupId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var group = await context.Groups.ArchiveGroupAsync(id, ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Unarchive()
    {
        var command = new Command("unarchive", "Return an archived group to the active list.") { GroupId };

        command.SetHandler(async (context, ct) =>
        {
            var group = await context.Groups.UnarchiveGroupAsync(context.ParseResult.GetValue(GroupId), ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Leave()
    {
        var command = new Command("leave", "Leave a group. Requires that your balance in it is settled.")
        {
            GroupId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);

            // Read first so the confirmation can name the group rather than echo a guid
            // back at someone who is about to lose access to it.
            var group = await context.Groups.GetGroupAsync(id, ct);

            Confirmation.Require(
                context,
                action: "groups.leave",
                summary: $"Leave the group '{group.Name}'?",
                changes: [$"You are removed from '{group.Name}' ({group.MemberCount} members)."]);

            await context.Groups.LeaveGroupAsync(id, ct);

            context.Output.Write(
                new { status = "left", groupId = id, name = group.Name },
                value => new Markup($"[green]Left[/] {Markup.Escape(value.name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Adds people to a group as guests, by name: somebody the group shares costs with who
    /// has no account in it.
    /// </summary>
    /// <remarks>
    /// No link unless <c>--link</c> asks for one. Most of the people a group splits with
    /// never sign up; the members record everything for them, and a link can be sent later
    /// with <c>guest-link create</c> if the real person wants in.
    /// </remarks>
    private static Command AddGuest()
    {
        var names = new Argument<string[]>("name")
        {
            Description = "One or more people to add, by name.",
            Arity = ArgumentArity.OneOrMore
        };

        var link = new Option<bool>("--link")
        {
            Description = "Issue each of them a link to take their place, straight away."
        };

        var command = new Command("add-guest", "Add people to a group as guests, by name.")
        {
            GroupId, names, link
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);

            var added = await context.Groups.AddGroupGuestsAsync(id, new AddGuestsRequest
            {
                Names = [.. context.ParseResult.GetValue(names) ?? []],
                IssueLinks = context.ParseResult.GetValue(link)
            }, ct);

            context.Output.Write(added, value => GuestTable(context, value));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>The group's guests, with whether each has a link out and until when.</summary>
    private static Command Guests()
    {
        var command = new Command("guests", "List a group's guests.") { GroupId };

        command.SetHandler(async (context, ct) =>
        {
            var guests = await context.Groups.GetGroupGuestsAsync(context.ParseResult.GetValue(GroupId), ct);

            context.Output.Write(guests, value => value.Count == 0
                ? new Markup(Tables.Empty("guests") + "\n")
                : GuestTable(context, value));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The user id first, because it is the one an agent needs next: it names the guest in
    /// a split, as the payer, in a repayment, and to remove them.
    /// </summary>
    /// <remarks>
    /// The link column is the URL when the CLI knows where the app is, and otherwise only says
    /// one is out. Never the bare token: it is a credential on the guest's position, and a
    /// table is what ends up pasted into a chat. JSON carries it for whoever needs it, and
    /// <c>guest-link create</c> prints it on purpose.
    /// <para>
    /// A working link says until when it works. One that ran out unclaimed reads "expired"
    /// in the table and gets a line of its own under it, with the day and what to run: it
    /// answers nobody, and <c>guest-link create</c> issues a new one. Under the table rather
    /// than in a column, because a fifth column wraps the user id in an 80-column terminal.
    /// </para>
    /// </remarks>
    private static IRenderable GuestTable(CliContext context, IEnumerable<GroupGuestResponse> guests)
    {
        var table = Tables.Grid("User id", "Name", "Added", "Link");
        var notes = new List<IRenderable>();

        foreach (var guest in guests)
        {
            var until = guest.LinkExpiresAt is { } expires
                ? expires.ToLocalTime().ToString("yyyy-MM-dd")
                : null;

            table.AddRow(
                guest.UserId.ToString(),
                Markup.Escape(guest.Name),
                guest.AddedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                guest.Token is { } token
                    ? WebLinks.Claim(context, token) is { } url
                        ? $"{Markup.Escape(url)} [grey]until {until}[/]"
                        : $"yes, until {until} [grey](guest-link create shows it)[/]"
                    : guest.LinkExpired
                        ? "[yellow]expired[/]"
                        : "[grey]none[/]");

            if (guest.LinkExpired)
            {
                notes.Add(new Markup(
                    $"[yellow]{Markup.Escape(guest.Name)}'s link expired on {until}.[/] [grey]For a new one:[/]\n" +
                    "[grey]  groupsplit groups guest-link create <group-id> <user-id>[/]\n"));
            }
        }

        return notes.Count == 0 ? table : new Rows([table, new Text(string.Empty), .. notes]);
    }

    /// <summary>
    /// A guest's link: issue one, replace it, or stop it.
    /// </summary>
    private static Command GuestLink()
    {
        var link = new Command("guest-link", "The link that lets the real person take a guest's place.");

        link.Subcommands.Add(CreateGuestLink());
        link.Subcommands.Add(RevokeGuestLink());

        return link;
    }

    private static readonly Argument<Guid> GuestId = new("user-id")
    {
        Description = "The guest's user id, as shown by `groupsplit groups guests`."
    };

    private static Command CreateGuestLink()
    {
        var replace = new Option<bool>("--new")
        {
            Description = "Replace the link the guest already has, so the old one stops working."
        };

        var command = new Command("create",
            "Show the guest's link, issuing one if there is none or it has expired.")
        {
            GroupId, GuestId, replace
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var userId = context.ParseResult.GetValue(GuestId);
            var fresh = context.ParseResult.GetValue(replace);

            // Read first, so the confirmation is only asked for when something is lost:
            // issuing a first link, reading back the one that is out, or replacing one that
            // has expired destroys nothing.
            var guest = (await context.Groups.GetGroupGuestsAsync(id, ct))
                        .FirstOrDefault(candidate => candidate.UserId == userId)
                        ?? throw NotAGuest(id);

            if (fresh && guest.HasLink)
            {
                Confirmation.Require(
                    context,
                    action: "groups.guest-link.create",
                    summary: $"Replace {guest.Name}'s link?",
                    changes:
                    [
                        "The link already sent stops working.",
                        "Whoever opened it is no longer offered it.",
                        $"{guest.Name} stays a guest, with everything recorded for them."
                    ]);
            }

            var linked = await context.Groups.IssueGuestLinkAsync(id, userId,
                new IssueGuestLinkRequest { Replace = fresh }, ct);

            context.Output.Write(
                new GuestLinkView(linked.UserId, linked.GroupId, linked.Name, linked.Token!,
                    WebLinks.Claim(context, linked.Token!), linked.LinkExpiresAt),
                value =>
                {
                    var table = Tables.KeyValue();

                    table.AddRow("Guest", Markup.Escape(value.Name));
                    table.AddRow("Link", Markup.Escape(value.Url ?? value.Token));

                    if (value.ExpiresAt is { } expires)
                        table.AddRow("Expires", expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

                    return new Rows(table, new Markup(
                        $"\n[grey]Whoever opens it and claims it takes {Markup.Escape(value.Name)}'s " +
                        "place in the group: everything recorded for them becomes theirs.[/]\n"));
                });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command RevokeGuestLink()
    {
        var command = new Command("revoke", "Stop a guest's link. The guest stays.")
        {
            GroupId, GuestId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var userId = context.ParseResult.GetValue(GuestId);

            var guest = (await context.Groups.GetGroupGuestsAsync(id, ct))
                        .FirstOrDefault(candidate => candidate.UserId == userId)
                        ?? throw NotAGuest(id);

            Confirmation.Require(
                context,
                action: "groups.guest-link.revoke",
                summary: $"Stop {guest.Name}'s link?",
                changes:
                [
                    "The link stops working for whoever has it.",
                    $"{guest.Name} stays a guest, with everything recorded for them.",
                    "A new link can be issued at any time."
                ]);

            await context.Groups.RevokeGuestLinkAsync(id, userId, ct);

            context.Output.Write(
                new { status = "revoked", groupId = id, userId, name = guest.Name },
                value => new Markup($"[green]Stopped[/] {Markup.Escape(value.name)}'s link.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static CliException NotAGuest(Guid groupId) =>
        new(new CliError(
                "That person is not a guest of this group.",
                Shared.Errors.ErrorCodes.GroupGuestNotFound,
                $"List the guests with: groupsplit groups guests {groupId}"),
            ExitCodes.InvalidInput);

    /// <summary>
    /// A guest's link as the CLI answers with it: the token, the URL it goes in, and when it
    /// stops working if nobody claims it.
    /// </summary>
    internal sealed record GuestLinkView(Guid UserId, Guid GroupId, string Name, string Token, string? Url,
        DateTimeOffset? ExpiresAt);

    // The three commands below call the routes from before guests, which the API marks
    // deprecated. They are kept as aliases on purpose, for one release.
#pragma warning disable CS0612
    /// <summary>
    /// Adds people as guests and issues each a link. Kept for scripts written before guests:
    /// <c>add-guest --link</c> is the same thing.
    /// </summary>
    private static Command Invite()
    {
        var names = new Argument<string[]>("name")
        {
            Description = "One or more people to add, by name.",
            Arity = ArgumentArity.OneOrMore
        };

        var command = new Command("invite",
            "Deprecated: use `add-guest --link`. Adds people as guests, with a link each.")
        {
            GroupId, names
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var invited = context.ParseResult.GetValue(names) ?? [];

            var request = new InviteToGroupRequest { Names = [.. invited] };

            var invitations = await context.Groups.InviteToGroupAsync(id, request, ct);

            context.Output.Write(invitations, value =>
            {
                var table = Tables.Grid("Invitation", "Name", "User id", "Link token");

                foreach (var invitation in value)
                {
                    table.AddRow(
                        invitation.Id.ToString(),
                        Markup.Escape(invitation.Name),
                        invitation.ParticipantUserId.ToString(),
                        Markup.Escape(invitation.Token));
                }

                return new Rows(
                    table,
                    new Markup("\n[grey]Each is a guest now. Send each person their own link: " +
                               "whoever claims it takes their place in the group.[/]\n"));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The guests with a link out, in the shape from before guests. Kept for scripts;
    /// <c>guests</c> lists every guest.
    /// </summary>
    private static Command Invitations()
    {
        var command = new Command("invitations",
            "Deprecated: use `guests`. Lists the guests that have a link out.")
        {
            GroupId
        };

        command.SetHandler(async (context, ct) =>
        {
            var invitations = await context.Groups.GetGroupInvitationsAsync(
                context.ParseResult.GetValue(GroupId), ct);

            context.Output.Write(invitations, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("guests with a link") + "\n");
                }

                var table = Tables.Grid("Id", "Name", "Link sent by", "When", "Expires");

                foreach (var invitation in value)
                {
                    table.AddRow(
                        invitation.Id.ToString(),
                        Markup.Escape(invitation.Name),
                        Markup.Escape(invitation.InvitedByUserName ?? "-"),
                        invitation.InvitedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                        invitation.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd"));
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Stops a guest's link by its old invitation id. Kept for scripts;
    /// <c>guest-link revoke</c> takes the guest's user id.
    /// </summary>
    private static Command Withdraw()
    {
        var invitationId = new Argument<Guid>("invitation-id")
        {
            Description = "The invitation's id, as shown by `groupsplit groups invitations`."
        };

        var command = new Command("withdraw-invitation",
            "Deprecated: use `guest-link revoke`. Stops a guest's link; the guest stays.")
        {
            GroupId, invitationId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var invitation = context.ParseResult.GetValue(invitationId);

            var pending = await context.Groups.GetGroupInvitationsAsync(id, ct);
            var withdrawn = pending.FirstOrDefault(candidate => candidate.Id == invitation);
            var name = withdrawn?.Name ?? invitation.ToString();

            Confirmation.Require(
                context,
                action: "groups.withdraw-invitation",
                summary: $"Stop {name}'s link?",
                changes:
                [
                    "The link stops working for whoever has it.",
                    $"{name} stays a guest, with everything recorded for them. Nothing moves.",
                    "To take them out of the group, settle with them and use remove-member."
                ]);

            var closed = await context.Groups.WithdrawGroupInvitationAsync(id, invitation, ct);

            context.Output.Write(closed, Closed);

            return ExitCodes.Success;
        });

        return command;
    }

#pragma warning restore CS0612

    /// <summary>
    /// A guest's link that was stopped or turned down. Shared with `invitations decline`,
    /// which is the same event from the other side.
    /// </summary>
    /// <remarks>
    /// Only the link ends, so there is nothing about money to report: the guest and
    /// everything recorded for them stay.
    /// </remarks>
    internal static IRenderable Closed(InvitationClosedResponse closed)
    {
        var table = Tables.KeyValue();

        table.AddRow("Group", Markup.Escape(closed.GroupName));
        table.AddRow("Guest", Markup.Escape(closed.Name));
        table.AddRow("Outcome", closed.Outcome is InvitationOutcome.Declined
            ? "link turned down"
            : "link stopped");

        return new Rows(table, new Markup(
            $"\n[grey]{Markup.Escape(closed.Name)} is still a guest of {Markup.Escape(closed.GroupName)}. " +
            "Nothing moved. A member can send a new link.[/]\n"));
    }

    private static IRenderable Render(GroupResponse group)
    {
        var table = Tables.KeyValue();
        table.AddRow("Id", group.Id.ToString());
        table.AddRow("Name", Markup.Escape(group.Name));
        if (!string.IsNullOrWhiteSpace(group.Description))
            table.AddRow("Description", Markup.Escape(group.Description));
        table.AddRow("Members", group.MemberCount.ToString());
        table.AddRow("State", group.IsArchive ? "archived" : "active");

        // Named and identified: the name is what somebody reads, and the id is what they
        // pass back to `set-default-split` or compare against `split-rules list`. A group
        // from before the built-in rules has neither, and says so rather than printing a
        // blank row.
        table.AddRow("Default split", group.DefaultSplitRuleId is { } ruleId
            ? $"{Markup.Escape(group.DefaultSplitRuleName ?? "unnamed")} ({ruleId})"
            : "[grey]none — divides evenly[/]");

        table.AddRow("Activity opens on", Periods.First(known => known.Period == group.DefaultPeriod).Name);

        return table;
    }
}
