using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
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
        groups.Subcommands.Add(Members());
        groups.Subcommands.Add(Balances());
        groups.Subcommands.Add(Archive());
        groups.Subcommands.Add(Unarchive());
        groups.Subcommands.Add(Leave());
        groups.Subcommands.Add(Invite());

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

                return table;
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
