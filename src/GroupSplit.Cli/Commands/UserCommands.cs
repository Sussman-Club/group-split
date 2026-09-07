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
