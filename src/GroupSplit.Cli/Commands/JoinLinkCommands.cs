using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// The group's side of a shareable join link: make one, read it back, put it out.
/// </summary>
/// <remarks>
/// A group has one link at a time, so these are singular even though the API answers with a
/// list -- the list is there because a race could leave two, and the newest is the one being
/// shared. The token on its own is not much use to a person, so text mode prints the URL a
/// browser would open. JSON gives both, since a caller composing its own message wants the
/// token and one composing a link wants the link.
/// </remarks>
public static class JoinLinkCommands
{
    private static readonly Argument<Guid> GroupId = new("group-id")
    {
        Description = "The group's id, as shown by `groupsplit groups list`."
    };

    public static Command Build()
    {
        var link = new Command("link", "The shareable link that lets anyone holding it join a group.");

        link.Subcommands.Add(Show());
        link.Subcommands.Add(Create());
        link.Subcommands.Add(Revoke());

        return link;
    }

    private static Command Show()
    {
        var command = new Command("show", "Show a group's standing join link.") { GroupId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);
            var link = await Current(context, id, ct);

            if (link is null)
            {
                // Not an error: a group nobody has made a link for is an ordinary group,
                // and a script asking after one wants an answer rather than an exit code.
                // `hasLink` and not a null member, because the JSON writer drops nulls --
                // an absent field would have to be told apart from a field it never emits.
                context.Output.Write(
                    new { groupId = id, hasLink = false },
                    _ => new Markup(Tables.Empty("join link") +
                                    $" [grey]Make one with: groupsplit groups link create {id}[/]\n"));

                return ExitCodes.Success;
            }

            context.Output.Write(Describe(context, link), value => Render(value));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Create()
    {
        var command = new Command("create",
            "Make a join link for a group. Any link the group already had stops working.")
        {
            GroupId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);

            // Read first, so the confirmation is only asked for when there is something to
            // lose. Making the first link of a group destroys nothing and should not stop
            // a script to ask about it.
            var standing = await Current(context, id, ct);

            if (standing is not null)
            {
                Confirmation.Require(
                    context,
                    action: "groups.link.create",
                    summary: $"Replace the join link for '{standing.GroupName}'?",
                    changes:
                    [
                        "The link you have already shared stops working.",
                        "Anyone who has not used it yet will need the new one.",
                        "People already in the group stay in."
                    ],
                    confirmCommand: $"groupsplit groups link create {id} --yes");
            }

            var link = await context.Groups.CreateGroupJoinLinkAsync(id, ct);

            context.Output.Write(Describe(context, link), value => Render(value));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Revoke()
    {
        var command = new Command("revoke", "Turn off a group's join link, so the URL stops working.")
        {
            GroupId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(GroupId);

            var group = await context.Groups.GetGroupAsync(id, ct);

            Confirmation.Require(
                context,
                action: "groups.link.revoke",
                summary: $"Turn off the join link for '{group.Name}'?",
                changes:
                [
                    "Anyone you have already sent it to will not be able to join with it.",
                    "People already in the group stay in."
                ],
                confirmCommand: $"groupsplit groups link revoke {id} --yes");

            await context.Groups.RevokeGroupJoinLinksAsync(id, ct);

            context.Output.Write(
                new { status = "revoked", groupId = id, name = group.Name },
                value => new Markup(
                    $"[green]Turned off[/] the join link for {Markup.Escape(value.name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The newest live link, or null. The API's list is ordered already; taking the first
    /// here says which one is meant rather than relying on that.
    /// </summary>
    private static async Task<GroupJoinLinkResponse?> Current(CliContext context, Guid groupId,
        CancellationToken ct)
    {
        var links = await context.Groups.GetGroupJoinLinksAsync(groupId, ct);

        return links.MaxBy(link => link.CreatedAt);
    }

    /// <summary>
    /// The link with the URL a browser would open written out, when the CLI has been told
    /// where the app is published. Pointed straight at the API and nowhere else, it has no
    /// way to know that, and the token alone is the honest answer.
    /// </summary>
    internal static JoinLinkView Describe(CliContext context, GroupJoinLinkResponse link) =>
        new(link.GroupId, link.GroupName, link.Token, Url(context, link.Token), link.CreatedByUserName,
            link.ExpiresAt);

    internal static string? Url(CliContext context, string token)
    {
        var web = context.Endpoints.WebOrNull;

        return web is null ? null : $"{web.GetLeftPart(UriPartial.Path).TrimEnd('/')}/join/{token}";
    }

    private static IRenderable Render(JoinLinkView view)
    {
        var table = Tables.KeyValue();

        table.AddRow("Group", Markup.Escape(view.GroupName));
        table.AddRow("Link", Markup.Escape(view.Url ?? view.Token));

        if (view.Url is null)
        {
            table.AddRow(string.Empty,
                "[grey]That is the token. Set a server origin to be shown the full URL:[/]\n"
                + "[grey]groupsplit config set server <url>[/]");
        }

        table.AddRow("Expires", view.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        table.AddRow("Made by", Markup.Escape(view.CreatedByUserName ?? "-"));

        return table;
    }

    /// <summary>
    /// What the CLI answers with: the link as the API gave it, plus the URL the token
    /// belongs in. Its own shape rather than the wire type, because the URL is this
    /// client's to compose and the API has no business knowing it.
    /// </summary>
    internal sealed record JoinLinkView(
        Guid GroupId,
        string GroupName,
        string Token,
        string? Url,
        string? CreatedByUserName,
        DateTimeOffset ExpiresAt);
}
