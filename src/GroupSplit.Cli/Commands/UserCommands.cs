using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

public static class UserCommands
{
    public static Command Build()
    {
        var users = new Command("users", "The signed-in account and what it is owed.");

        users.Subcommands.Add(Me());
        users.Subcommands.Add(Position());
        users.Subcommands.Add(Delete());

        return users;
    }

    private static Command Me()
    {
        var command = new Command("me", "Show the account this CLI is acting as.");

        command.SetHandler(async (context, ct) =>
        {
            var user = await context.Users.GetCurrentUserAsync(ct);

            context.Output.Write(user, value =>
            {
                var table = Tables.KeyValue();
                table.AddRow("Id", value.Id.ToString());
                table.AddRow("Name", Markup.Escape(value.FullName));
                table.AddRow("Email", Markup.Escape(value.Email ?? "-"));

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Position()
    {
        var command = new Command("position", "Show your overall balance across every group.");

        command.SetHandler(async (context, ct) =>
        {
            var position = await context.Users.GetCurrentUserPositionAsync(ct);

            context.Output.Write(position, value =>
            {
                var rows = new Rows(
                    new Markup(
                        $"Net [bold]{Tables.Money(value.Net)}[/]  "
                        + $"owed to you [green]{value.OwedToYou:N2}[/]  "
                        + $"you owe [red]{value.YouOwe:N2}[/]\n"),
                    GroupTable(value.Groups));

                return rows;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Deletes the signed-in account. The one command here that cannot be undone, so the
    /// confirmation names the account rather than describing it in the abstract, and the
    /// stored credentials go with it -- leaving them would make the next command fail
    /// against an identity that no longer exists.
    /// </summary>
    private static Command Delete()
    {
        var command = new Command("delete", "Delete the account you are signed in as. Permanent.");

        command.SetHandler(async (context, ct) =>
        {
            var user = await context.Users.GetCurrentUserAsync(ct);

            Confirmation.Require(
                context,
                action: "users.delete",
                summary: $"Permanently delete the account {user.Email ?? user.FullName}?",
                changes:
                [
                    $"{user.FullName} is removed from every group.",
                    "Your own expenses and their history go with it. This cannot be undone.",
                    "Refused while you still owe money or are owed it in any group."
                ],
                confirmCommand: "groupsplit users delete --yes");

            await context.Users.DeleteCurrentUserAsync(ct);

            // The account is gone, so credentials for it can only produce failures. Cleared
            // after the call, never before: a refusal -- an unsettled balance is one -- has
            // to leave the session it arrived on intact. AuthorityOrNull rather than
            // Authority, because GROUPSPLIT_TOKEN is a complete configuration with no
            // identity server named, and there is nothing on disk to clear in that case.
            if (context.Endpoints.AuthorityOrNull is { } authority)
            {
                context.Tokens.Remove(authority, context.Endpoints.ClientId);
            }

            context.Output.Write(
                new { status = "deleted", userId = user.Id },
                _ => new Markup("[green]Account deleted.[/] Signed out.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static IRenderable GroupTable(IEnumerable<Shared.GroupPosition> groups)
    {
        var list = groups.ToList();

        if (list.Count == 0)
        {
            return new Markup(Tables.Empty("groups"));
        }

        var table = Tables.Grid("Group", "Balance");

        foreach (var group in list)
        {
            table.AddRow(
                Markup.Escape(group.GroupName) + (group.IsArchive ? " [grey](archived)[/]" : string.Empty),
                Tables.Money(group.Balance));
        }

        return table;
    }
}
