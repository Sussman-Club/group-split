using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

public static class InvitationCommands
{
    private static readonly Argument<Guid> InvitationId = new("invitation-id")
    {
        Description = "The invitation's id, as shown by `groupsplit invitations list`."
    };

    public static Command Build()
    {
        var invitations = new Command("invitations", "Group invitations addressed to you.");

        invitations.Subcommands.Add(List());
        invitations.Subcommands.Add(Accept());
        invitations.Subcommands.Add(Decline());

        return invitations;
    }

    private static Command List()
    {
        var command = new Command("list", "List invitations waiting for your answer.");

        command.SetHandler(async (context, ct) =>
        {
            var invitations = await new Api.InvitationsClient(context.ApiHttpClient).GetMyInvitationsAsync(ct);

            context.Output.Write(invitations, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("pending invitations") + "\n");
                }

                var table = Tables.Grid("Id", "Group", "Invited by", "When");

                foreach (var invitation in value)
                {
                    table.AddRow(
                        invitation.Id.ToString(),
                        Markup.Escape(invitation.GroupName),
                        Markup.Escape(invitation.InvitedByUserName ?? "-"),
                        invitation.InvitedAt.ToLocalTime().ToString("yyyy-MM-dd"));
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Accept()
    {
        var command = new Command("accept", "Accept an invitation and join the group.") { InvitationId };

        command.SetHandler(async (context, ct) =>
        {
            var group = await new Api.InvitationsClient(context.ApiHttpClient)
                .AcceptInvitationAsync(context.ParseResult.GetValue(InvitationId), ct);

            context.Output.Write(group, value => new Markup(
                $"[green]Joined[/] {Markup.Escape(value.Name)} [grey]{value.Id}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Decline()
    {
        var command = new Command("decline", "Decline an invitation.") { InvitationId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(InvitationId);

            await new Api.InvitationsClient(context.ApiHttpClient).DeclineInvitationAsync(id, ct);

            context.Output.Write(
                new { status = "declined", invitationId = id },
                _ => new Markup("[green]Declined.[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
