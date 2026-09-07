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
        var command = new Command("members", "List a group's members.") { GroupId };

        command.SetHandler(async (context, ct) =>
        {
            var members = await context.Groups.GetGroupMembersAsync(context.ParseResult.GetValue(GroupId), ct);

            context.Output.Write(members, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("members") + "\n");
                }

                var table = Tables.Grid("Id", "Name", "Email");

                foreach (var member in value)
                {
                    table.AddRow(
                        member.Id.ToString(),
                        Markup.Escape(member.FullName),
                        Markup.Escape(member.Email ?? "-"));
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
                    table.AddRow(
                        Markup.Escape(balance.UserName),
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
    /// Squares the whole group up at once, over whatever part of it is being settled.
    /// </summary>
    /// <remarks>
    /// With no options it settles everything outstanding, which is what most groups want
    /// most of the time and so is what it costs nothing to ask for. The scope options narrow
    /// it -- <c>--to</c> closes a month, <c>--category</c> settles one kind of spending --
    /// and every one of them is a field of the same filter <c>transactions list</c> takes.
    /// <para>
    /// Always previews first, so the confirmation shows the payments it is about to write
    /// rather than a description of them.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The one listing that shows transfers as well as expenses, which is why it is not
    /// <c>transactions list --group</c>: "Omar paid you 40" is the row people look for when
    /// a balance moves, and every other expense surface cannot see it.
    /// </summary>
    private static Command Activity()
    {
        var sortBy = new Option<string?>("--sort-by")
        {
            Description = "dateTime, amount or name. Defaults to dateTime."
        };

        var order = Sorting.Order();
        var page = new Option<int?>("--page") { Description = "1-based page number." };
        var pageSize = new Option<int?>("--page-size")
        {
            Description = "Rows per page. The server caps this."
        };

        var command = new Command("activity", "List everything that happened in a group, newest first.")
        {
            GroupId, sortBy, order, page, pageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var activity = await context.Groups.GetGroupActivityAsync(
                id: parse.GetValue(GroupId),
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

                var table = Tables.Grid("Date", "Kind", "What", "Amount", "Paid by", "Paid to");

                foreach (var entry in value.Items)
                {
                    table.AddRow(
                        entry.DateTime.ToLocalTime().ToString("yyyy-MM-dd"),
                        entry.Kind == ActivityKind.Transfer ? "transfer" : "expense",
                        Markup.Escape(entry.Name),
                        entry.Amount.ToString("N2"),
                        Markup.Escape(entry.PaidByUserName),
                        Markup.Escape(entry.PaidToUserName ?? "-"));
                }

                return new Rows(table, Tables.PageFooter(value));
            });

            return ExitCodes.Success;
        });

        return command;
    }

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

    private static Command Invite()
    {
        var emails = new Argument<string[]>("email")
        {
            Description = "One or more email addresses to invite.",
            Arity = ArgumentArity.OneOrMore
        };

        var command = new Command("invite", "Invite people to a group by email.") { GroupId, emails };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var addresses = context.ParseResult.GetValue(emails) ?? [];

            var request = new AddMemberRequest(
                addresses.Select(email => new UserIdentifier { Email = email }).ToHashSet());

            var invitations = await context.Groups.InviteToGroupAsync(id, request, ct);

            context.Output.Write(invitations, value =>
            {
                var table = Tables.Grid("Invitation", "Email", "Group");

                foreach (var invitation in value)
                {
                    table.AddRow(
                        invitation.Id.ToString(),
                        Markup.Escape(invitation.Email),
                        Markup.Escape(invitation.GroupName));
                }

                return table;
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

                var table = Tables.Grid("Id", "Email", "Invited by", "When");

                foreach (var invitation in value)
                {
                    table.AddRow(
                        invitation.Id.ToString(),
                        Markup.Escape(invitation.Email),
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

            await context.Groups.WithdrawGroupInvitationAsync(id, invitation, ct);

            // No confirmation: an unanswered invitation can be sent again, so nothing here
            // is lost that `groups invite` cannot put back.
            context.Output.Write(
                new { status = "withdrawn", groupId = id, invitationId = invitation },
                _ => new Markup("[green]Withdrawn.[/]\n"));

            return ExitCodes.Success;
        });

        return command;
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
