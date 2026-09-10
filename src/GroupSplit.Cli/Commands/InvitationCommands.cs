using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// Answering an invitation somebody sent you, and the group links anyone may follow.
/// </summary>
/// <remarks>
/// A link is how somebody learns of an invitation: it used to be an email address, so the
/// API could hand out every group waiting on you, and a named person and a link have no
/// address to match. What <c>list</c> answers instead is every invitation whose link you
/// have already opened -- which is the way back to one when the message it arrived in is
/// gone. Everything else here takes the link.
/// </remarks>
public static class InvitationCommands
{
    /// <summary>
    /// The random part of a URL somebody was sent. Taken as a whole URL too, because what a
    /// person has to hand is the link, not the token inside it, and making them cut it up by
    /// hand is a step for nothing.
    /// </summary>
    private static readonly Argument<string> Link = new("link")
    {
        Description = "The invitation link you were sent, or just the token at the end of one."
    };

    private static readonly Argument<string> JoinLink = new("link")
    {
        Description = "A group join link, or just the token at the end of one."
    };

    public static Command Build()
    {
        var invitations = new Command("invitations", "Invitations and join links you were sent.");

        invitations.Subcommands.Add(List());
        invitations.Subcommands.Add(Show());
        invitations.Subcommands.Add(Claim());
        invitations.Subcommands.Add(Decline());
        invitations.Subcommands.Add(ShowLink());
        invitations.Subcommands.Add(JoinByLink());

        return invitations;
    }

    /// <summary>
    /// The invitations whose links you have opened and not yet answered.
    /// </summary>
    /// <remarks>
    /// Not "every invitation sent to you", which nothing can answer any more: an invitation
    /// names a person and carries a link, and there is no address to match against an
    /// account. Opening a link is what puts it here, so this is the way back to one when the
    /// message it arrived in is gone -- which is exactly the case somebody hits after
    /// opening a link, signing in, and getting on with something else.
    /// </remarks>
    private static Command List()
    {
        var command = new Command("list", "List invitations you have opened and not answered.");

        command.SetHandler(async (context, ct) =>
        {
            var invitations = await new Api.InvitationsClient(context.ApiHttpClient).GetMyInvitationsAsync(ct);

            context.Output.Write(invitations, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("open invitations") + "\n");
                }

                // The token, because it is what every other command here takes -- and
                // because getting back to a link is the whole reason this exists.
                var table = Tables.Grid("Group", "Invited as", "Invited by", "When", "Link token");

                foreach (var invitation in value)
                {
                    table.AddRow(
                        Markup.Escape(invitation.GroupName),
                        Markup.Escape(invitation.Name),
                        Markup.Escape(invitation.InvitedByUserName ?? "-"),
                        invitation.InvitedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                        Markup.Escape(invitation.Token));
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// What an invitation link leads to, without claiming it.
    /// </summary>
    /// <remarks>
    /// It says nothing about the money, and that is the API's decision rather than this
    /// command's: a link can be forwarded, and what a group has recorded against the person
    /// it named is the group's until somebody claims it.
    /// </remarks>
    private static Command Show()
    {
        var command = new Command("show", "Show what an invitation link leads to, without claiming it.")
        {
            Link
        };

        command.SetHandler(async (context, ct) =>
        {
            var invitation = await new Api.InvitationsClient(context.ApiHttpClient)
                .GetInvitationClaimAsync(TokenIn(context.ParseResult.GetValue(Link)), ct);

            context.Output.Write(invitation, value =>
            {
                var table = Tables.KeyValue();

                table.AddRow("Group", Markup.Escape(value.GroupName));
                table.AddRow("Members", value.MemberCount.ToString());
                table.AddRow("Invited as", Markup.Escape(value.Name));
                table.AddRow("Invited by", Markup.Escape(value.InvitedByUserName ?? "-"));
                table.AddRow("When", value.InvitedAt.ToLocalTime().ToString("yyyy-MM-dd"));
                table.AddRow("You", value.AlreadyAMember ? "already a member" : "not a member yet");

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Taking it: joining the group and becoming the person the invitation names.
    /// </summary>
    /// <remarks>
    /// Gated, and not because it is hard to undo. Claiming takes on a position -- the shares
    /// the group has recorded against that person, and the expenses they are down as having
    /// paid for -- so it changes what the caller owes the moment it runs. The confirmation
    /// is where somebody finds that out before it is true rather than after.
    /// </remarks>
    private static Command Claim()
    {
        var command = new Command("claim", "Claim an invitation: join the group as the person it names.")
        {
            Link
        };

        command.SetHandler(async (context, ct) =>
        {
            var token = TokenIn(context.ParseResult.GetValue(Link));
            var client = new Api.InvitationsClient(context.ApiHttpClient);

            // Read first, so the confirmation names the group and the person rather than
            // quoting a token back at somebody.
            var invitation = await client.GetInvitationClaimAsync(token, ct);

            Confirmation.Require(
                context,
                action: "invitations.claim",
                summary: $"Join {invitation.GroupName} as {invitation.Name}?",
                changes:
                [
                    invitation.AlreadyAMember
                        ? $"You are already in {invitation.GroupName}."
                        : $"You join {invitation.GroupName} ({invitation.MemberCount} members).",
                    $"Everything the group has recorded against {invitation.Name} becomes " +
                    "yours: their shares, anything they are down as having paid for, and " +
                    "their places in the group's split rules.",
                    "No amount changes, but your balance in the group will.",
                    "The link stops working afterwards."
                ],
                confirmCommand: $"groupsplit invitations claim {token} --yes");

            var claimed = await client.ClaimInvitationAsync(token, ct);

            context.Output.Write(claimed, value =>
            {
                var lines = new List<IRenderable>
                {
                    new Markup($"[green]Joined[/] {Markup.Escape(value.GroupName)} " +
                               $"as {Markup.Escape(value.Name)} [grey]{value.GroupId}[/]\n")
                };

                if (value.TookAnything)
                {
                    var table = Tables.KeyValue();

                    table.AddRow("Shares taken on", $"{value.SharesTaken} ({value.AmountOwed:N2})");
                    table.AddRow("Payments taken on", $"{value.PaymentsTaken} ({value.AmountPaid:N2})");

                    // The row about the future rather than the past: a rule place gives
                    // them a share of every expense filed under that category from here
                    // on, which the two counts above cannot say.
                    table.AddRow("Rules naming you", $"{value.RulesTaken}");

                    lines.Add(table);
                    lines.Add(new Markup("\n[grey]No amount changed. What was recorded against " +
                                         "that name is recorded against you now.[/]\n"));
                }

                return new Rows(lines);
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Turning one down, behind the confirmation gate.
    /// </summary>
    /// <remarks>
    /// Gated because it can move money and not because it is hard to undo -- the group can
    /// ask again. Naming somebody makes them a participant in the group's spending straight
    /// away, so by the time somebody declines there may be a fortnight of shares in their
    /// name; those go to a member of the group, and the answer says whose they are now.
    /// </remarks>
    private static Command Decline()
    {
        var command = new Command("decline", "Decline an invitation you were sent.") { Link };

        command.SetHandler(async (context, ct) =>
        {
            var token = TokenIn(context.ParseResult.GetValue(Link));
            var client = new Api.InvitationsClient(context.ApiHttpClient);

            var invitation = await client.GetInvitationClaimAsync(token, ct);

            Confirmation.Require(
                context,
                action: "invitations.decline",
                summary: $"Decline the invitation to {invitation.GroupName}?",
                changes:
                [
                    "Nobody joins, and the link stops working.",
                    $"Anything the group had already recorded against {invitation.Name} -- " +
                    "shares, and anything they are down as having paid for -- goes to a " +
                    "member of that group.",
                    "No amount changes.",
                    "They can invite again, with a new link."
                ],
                confirmCommand: $"groupsplit invitations decline {token} --yes");

            var closed = await client.DeclineInvitationAsync(token, ct);

            context.Output.Write(closed, GroupCommands.Closed);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command ShowLink()
    {
        var command = new Command("link", "Show which group a join link leads to, without joining.")
        {
            JoinLink
        };

        command.SetHandler(async (context, ct) =>
        {
            var link = await new Api.InvitationsClient(context.ApiHttpClient)
                .GetJoinLinkAsync(TokenIn(context.ParseResult.GetValue(JoinLink)), ct);

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
        var command = new Command("join", "Join the group a join link leads to, as yourself.")
        {
            JoinLink
        };

        command.SetHandler(async (context, ct) =>
        {
            var joined = await new Api.InvitationsClient(context.ApiHttpClient)
                .AcceptJoinLinkAsync(TokenIn(context.ParseResult.GetValue(JoinLink)), ct);

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
}
