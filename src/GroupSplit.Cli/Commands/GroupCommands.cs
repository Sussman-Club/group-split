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
        groups.Subcommands.Add(Members());
        groups.Subcommands.Add(RemoveMember());
        groups.Subcommands.Add(Balances());
        groups.Subcommands.Add(Settle());
        groups.Subcommands.Add(SettleBetween());
        groups.Subcommands.Add(SettleUp());
        groups.Subcommands.Add(Activity());
        groups.Subcommands.Add(Archive());
        groups.Subcommands.Add(Unarchive());
        groups.Subcommands.Add(Leave());
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
            var groups = await context.Groups.GetGroupsAsync(ct);

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
                        group.IsArchive ? "[grey]archived[/]" : "active");
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
        var command = new Command("create", "Create a group.") { name };

        command.SetHandler(async (context, ct) =>
        {
            var group = await context.Groups.CreateGroupAsync(
                new CreateGroupRequest { Name = context.ParseResult.GetValue(name)! }, ct);

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
        var command = new Command("rename", "Rename a group.") { GroupId, name };

        command.SetHandler(async (context, ct) =>
        {
            var patch = new JsonPatchDocument<CreateGroupRequest>();
            patch.Replace(request => request.Name, context.ParseResult.GetValue(name)!);

            var group = await context.Groups.UpdateGroupAsync(
                context.ParseResult.GetValue(GroupId), patch, ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Members()
    {
        var command = new Command("members",
            "List a group's members, and the people it has invited and is waiting on.")
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
                // to find the id to put in a split or name as the payer, and an invited
                // address is choosable for both -- while being nobody you can settle up
                // with, and nobody who can see the group.
                var table = Tables.Grid("Id", "Name", "Email", "Status");

                foreach (var member in value)
                {
                    table.AddRow(
                        member.Id.ToString(),
                        Markup.Escape(member.FullName),
                        Markup.Escape(member.Email ?? "-"),
                        member.IsPendingInvitee ? "[yellow]invited[/]" : "joined");
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Removing somebody else, which is not the same command as leaving: only a member with
    /// a settled balance can go, and the API refuses the rest.
    /// </summary>
    private static Command RemoveMember()
    {
        var userId = new Argument<Guid>("user-id")
        {
            Description = "The member's id, as shown by `groupsplit groups members`."
        };

        var command = new Command("remove-member", "Remove a member from a group.")
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
            var removed = members.FirstOrDefault(candidate => candidate.Id == member);
            var name = removed?.FullName ?? member.ToString();

            Confirmation.Require(
                context,
                action: "groups.remove-member",
                summary: $"Remove {name} from the group?",
                changes:
                [
                    $"{name} loses access to the group and its history.",
                    "Refused if their balance in it is not settled."
                ],
                confirmCommand: $"groupsplit groups remove-member {id} {member} --yes");

            var group = await context.Groups.RemoveGroupMemberAsync(id, member, ct);

            context.Output.Write(group, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Balances()
    {
        var command = new Command("balances", "Show who owes whom in a group.") { GroupId };

        command.SetHandler(async (context, ct) =>
        {
            var balances = await context.Groups.GetGroupUserBalanceAsync(context.ParseResult.GetValue(GroupId), ct);

            context.Output.Write(balances, value =>
            {
                var table = Tables.Grid("Member", "Paid", "Owed", "Balance");

                foreach (var balance in value.NetBalances)
                {
                    // Somebody invited and still to answer is in this column, because the
                    // column has to add up to zero, and out of the transfers below, because
                    // there is no account to pay yet. The marker is what keeps that from
                    // looking like a plan that forgot somebody.
                    table.AddRow(
                        Markup.Escape(balance.UserName) +
                        (balance.IsPendingInvitee ? " [yellow](invited)[/]" : string.Empty),
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
        var userId = new Argument<Guid>("user-id") { Description = "The other member." };
        var amount = new Argument<decimal>("amount")
        {
            Description = "How much changed hands, to two decimal places."
        };

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

        var command = new Command("settle", "Record a repayment between you and another member.")
        {
            GroupId, userId, amount, direction, date, note
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

            var members = await context.Groups.GetGroupMembersAsync(id, ct);
            var name = members.FirstOrDefault(candidate => candidate.Id == other)?.FullName
                       ?? other.ToString();

            Confirmation.Require(
                context,
                action: "groups.settle",
                summary: side == SettlementDirection.TheyPaidYou
                    ? $"Record that {name} paid you {paid:N2}?"
                    : $"Record that you paid {name} {paid:N2}?",
                changes: [$"A transfer of {paid:N2} is written, and both balances move by it."],
                confirmCommand: $"groupsplit groups settle {id} {other} {paid} "
                                + $"--direction {side.ToString().ToLowerInvariant()} --yes");

            await context.Groups.SettleGroupDebtsAsync(
                id,
                new SettleRequest
                {
                    UserId = other,
                    Amount = paid,
                    Direction = side,
                    Date = when,
                    Description = description
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
    /// Records one repayment between two named members, whichever of them you are --
    /// including neither.
    /// </summary>
    /// <remarks>
    /// Separate from <c>settle</c> and <c>settle-up</c> because both of those put you on one
    /// end, and saying something about two other people should have to be asked for by name.
    /// <para>
    /// What it is for is a ledger the group already agreed on: months closed in a spreadsheet
    /// years ago, moving in. Their repayments have to be written for the imbalance their
    /// expenses carry to ever clear, and whoever is doing the moving is on neither end of
    /// most of them.
    /// </para>
    /// </remarks>
    private static Command SettleBetween()
    {
        var fromUserId = new Argument<Guid>("from-user-id") { Description = "The member who paid." };
        var toUserId = new Argument<Guid>("to-user-id") { Description = "The member who was paid." };

        var amount = new Argument<decimal>("amount")
        {
            Description = "How much changed hands, to two decimal places."
        };

        var date = new Option<DateTimeOffset?>("--date")
        {
            Description = "When the money moved, e.g. 2026-09-30. Defaults to now."
        };

        var note = new Option<string?>("--note")
        {
            Description = "What to remember about it, e.g. \"end of September 2024\"."
        };

        var command = new Command("settle-between",
            "Record a repayment between two members, whoever you are.")
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
                ],
                confirmCommand: $"groupsplit groups settle-between {id} {payer} {payee} {paid} --yes");

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

        var command = new Command("settle-up", "Square up with everybody in a group at once.")
        {
            GroupId, date, note, dryRun
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(GroupId);

            var balance = await context.Groups.GetGroupUserBalanceAsync(id, ct);

            var payments = new List<(string Line, decimal Amount)>();

            payments.AddRange(balance.YouOwed.Select(debt =>
                ($"You pay {debt.UserName} {debt.Amount:N2}.", debt.Amount)));

            payments.AddRange(balance.OwedToYou.Select(credit =>
                ($"{credit.UserName} pays you {credit.Amount:N2}.", credit.Amount)));

            if (payments.Count == 0)
            {
                context.Output.Write(
                    new { status = "already-square", groupId = id },
                    _ => new Markup("You are already square with everybody in this group.\n"));

                return ExitCodes.Success;
            }

            if (parse.GetValue(dryRun))
            {
                context.Output.Write(
                    new { groupId = id, payments = payments.Select(payment => payment.Line) },
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
                changes: [.. payments.Select(payment => payment.Line)],
                confirmCommand: $"groupsplit groups settle-up {id} --yes");

            var settled = await context.Groups.SettleUpAsync(
                id,
                new SettleUpRequest
                {
                    Date = parse.GetValue(date),
                    Description = parse.GetValue(note)
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

        var order = Sorting.Order();
        var page = new Option<int?>("--page") { Description = "1-based page number." };
        var pageSize = new Option<int?>("--page-size")
        {
            Description = "Rows per page. The server caps this."
        };

        var command = new Command("activity", "List everything that happened in a group, newest first.")
        {
            GroupId, kind, from, to, search, sortBy, order, page, pageSize
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
                changes: [$"You are removed from '{group.Name}' ({group.MemberCount} members)."],
                confirmCommand: $"groupsplit groups leave {id} --yes");

            await context.Groups.LeaveGroupAsync(id, ct);

            context.Output.Write(
                new { status = "left", groupId = id, name = group.Name },
                value => new Markup($"[green]Left[/] {Markup.Escape(value.name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Inviting people by name, which mints a link for each.
    /// </summary>
    /// <remarks>
    /// By name rather than by email, because a group knows the friend it went on the trip
    /// with by name. The links are the output that matters: they are how the invitation
    /// reaches anybody, and each one works once.
    /// </remarks>
    private static Command Invite()
    {
        var names = new Argument<string[]>("name")
        {
            Description = "One or more people to invite, by name.",
            Arity = ArgumentArity.OneOrMore
        };

        var command = new Command("invite", "Invite people to a group by name, with a link each.")
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
                // The user id is in the table because it is the one an agent needs next: it
                // names this person in a split or as the payer, without waiting for them to
                // claim anything. The token is the thing to send them.
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
                    new Markup("\n[grey]Send each person their own link. It works once, and " +
                               "whoever opens it becomes that person in the group.[/]\n"));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The invitations a group is waiting on, which is the other direction from
    /// <c>groupsplit invitations list</c>: this is the sender's view, and all it knows about
    /// an invitee is the address, since an invited address need not have an account yet.
    /// </summary>
    private static Command Invitations()
    {
        var command = new Command("invitations", "List the invitations a group is still waiting on.")
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
                    return new Markup(Tables.Empty("outstanding invitations") + "\n");
                }

                // The id here withdraws the invitation. The id that names this person in a
                // split or as a payer is a different one -- participantUserId, which is in
                // the JSON alongside the link token, and which `groups members` lists them
                // under, marked as invited. Three guids in one table is unreadable.
                var table = Tables.Grid("Id", "Name", "Invited by", "When");

                foreach (var invitation in value)
                {
                    table.AddRow(
                        invitation.Id.ToString(),
                        Markup.Escape(invitation.Name),
                        Markup.Escape(invitation.InvitedByUserName ?? "-"),
                        invitation.InvitedAt.ToLocalTime().ToString("yyyy-MM-dd"));
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Withdraw()
    {
        var invitationId = new Argument<Guid>("invitation-id")
        {
            Description = "The invitation's id, as shown by `groupsplit groups invitations`."
        };

        var command = new Command("withdraw-invitation", "Withdraw an invitation nobody has answered.")
        {
            GroupId, invitationId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var invitation = context.ParseResult.GetValue(invitationId);

            // Read first, so the confirmation can name the address and say what is
            // riding on it. An invitation used to be a message and withdrawing it needed no
            // gate; it is a participant in the group's money now, so it does.
            var pending = await context.Groups.GetGroupInvitationsAsync(id, ct);
            var withdrawn = pending.FirstOrDefault(candidate => candidate.Id == invitation);
            var name = withdrawn?.Name ?? invitation.ToString();

            Confirmation.Require(
                context,
                action: "groups.withdraw-invitation",
                summary: $"Withdraw the invitation to {name}?",
                changes:
                [
                    $"Their link stops working, and {name} stops being someone this group " +
                    "can give a share to.",
                    "Anything already recorded against them -- shares, and anything they " +
                    "paid for -- goes to a member of the group.",
                    "No amount changes, and the group's balances still add up.",
                    "They can be invited again, with a new link."
                ],
                confirmCommand: $"groupsplit groups withdraw-invitation {id} {invitation} --yes");

            var closed = await context.Groups.WithdrawGroupInvitationAsync(id, invitation, ct);

            context.Output.Write(closed, Closed);

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// What happened to an invitation that was withdrawn or declined, and to the money that
    /// was recorded against it.
    /// </summary>
    /// <remarks>
    /// Shared with `invitations decline`, because the two commands are the same event from
    /// the two sides and there is one answer to give about the money.
    /// </remarks>
    internal static IRenderable Closed(InvitationClosedResponse closed)
    {
        var table = Tables.KeyValue();

        table.AddRow("Group", Markup.Escape(closed.GroupName));
        table.AddRow("Name", Markup.Escape(closed.Name));
        table.AddRow("Outcome", closed.Outcome.ToString().ToLowerInvariant());

        if (!closed.MovedAnything)
        {
            table.AddRow("Money", "nothing was recorded against them");

            return table;
        }

        table.AddRow("Now belongs to", Markup.Escape(closed.AbsorbedByUserName ?? "-"));
        table.AddRow("Shares moved", $"{closed.SharesMoved} ({closed.AmountOwed:N2})");
        table.AddRow("Payments moved", $"{closed.PaymentsMoved} ({closed.AmountPaid:N2})");
        table.AddRow("Rules pruned", closed.RulesAffected.ToString());

        return new Rows(
            table,
            new Markup("\n[grey]No amount changed. Every transaction still sums to its own " +
                       "amount, and the group's balances still sum to zero.[/]\n"));
    }

    private static IRenderable Render(GroupResponse group)
    {
        var table = Tables.KeyValue();
        table.AddRow("Id", group.Id.ToString());
        table.AddRow("Name", Markup.Escape(group.Name));
        table.AddRow("Members", group.MemberCount.ToString());
        table.AddRow("State", group.IsArchive ? "archived" : "active");

        return table;
    }
}
