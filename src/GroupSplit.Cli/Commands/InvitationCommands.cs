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

    /// <summary>
    /// The random part of a shared URL. Taken as a whole URL too, because what somebody has
    /// to hand is the link, not the token inside it, and making them cut it up by hand is
    /// a step for nothing.
    /// </summary>
    private static readonly Argument<string> Token = new("link")
    {
        Description = "A join link, or just the token at the end of one."
    };

    public static Command Build()
    {
        var invitations = new Command("invitations", "Group invitations addressed to you.");

        invitations.Subcommands.Add(List());
        invitations.Subcommands.Add(Accept());
        invitations.Subcommands.Add(Decline());
        invitations.Subcommands.Add(ShowLink());
        invitations.Subcommands.Add(JoinByLink());

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

    private static Command ShowLink()
    {
        var command = new Command("link", "Show which group a join link leads to, without joining.")
        {
            Token
        };

        command.SetHandler(async (context, ct) =>
        {
            var link = await new Api.InvitationsClient(context.ApiHttpClient)
                .GetJoinLinkAsync(TokenIn(context.ParseResult.GetValue(Token)), ct);

            context.Output.Write(link, value =>
            {
                var table = Tables.KeyValue();

                table.AddRow("Group", Markup.Escape(value.GroupName));
                table.AddRow("Members", value.MemberCount.ToString());
                table.AddRow("Shared by", Markup.Escape(value.CreatedByUserName ?? "-"));
                table.AddRow("Expires", value.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                table.AddRow("You", value.AlreadyAMember ? "already a member" : "not a member yet");

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command JoinByLink()
    {
        var command = new Command("join", "Join the group a link leads to.") { Token };

        command.SetHandler(async (context, ct) =>
        {
            var joined = await new Api.InvitationsClient(context.ApiHttpClient)
                .AcceptJoinLinkAsync(TokenIn(context.ParseResult.GetValue(Token)), ct);

            // Already being in it is a success and reads as one. Following the same link
            // twice is the ordinary thing to do with a URL somebody was sent, and there is
            // one membership at the end of it either way.
            context.Output.Write(joined, value => new Markup(
                value.AlreadyAMember
                    ? $"[green]Already in[/] {Markup.Escape(value.GroupName)} [grey]{value.GroupId}[/]\n"
                    : $"[green]Joined[/] {Markup.Escape(value.GroupName)} [grey]{value.GroupId}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The token out of whatever was pasted: the last path segment of a URL, or the value
    /// itself when it is already just the token.
    /// </summary>
    private static string TokenIn(string? value)
    {
        var text = (value ?? string.Empty).Trim();

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return text;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 0 ? text : segments[^1];
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
