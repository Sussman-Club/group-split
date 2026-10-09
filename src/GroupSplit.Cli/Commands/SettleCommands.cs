using System.CommandLine;
using System.Globalization;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// Squaring up across every group at once: the plan, the payments, and the history.
/// </summary>
/// <remarks>
/// The counterpart of <c>groups settle</c> and <c>groups settle-up</c>, one level up. Those
/// two work inside a group, because a balance belongs to a group; these work on a person,
/// because a payment does. Somebody who owes the same friend in two groups pays them once,
/// and the API writes the per-group transfers behind it.
/// </remarks>
public static class SettleCommands
{
    public static Command Build()
    {
        var settle = new Command("settle", "Clear what you owe, across every group at once.");

        settle.Subcommands.Add(Plan());
        settle.Subcommands.Add(Pay());
        settle.Subcommands.Add(History());

        return settle;
    }

    /// <summary>
    /// What clears everything, in the fewest payments, grouped by person.
    /// </summary>
    private static Command Plan()
    {
        var through = ThroughOption.Create();

        var command = new Command("plan", "Show the fewest payments that leave you square everywhere.")
        {
            through
        };

        command.SetHandler(async (context, ct) =>
        {
            var day = context.ParseResult.GetValue(through);

            var plan = await context.Users.GetSettlementPlanAsync(day, ThroughOption.OffsetFor(day), ct);

            context.Output.Write(plan, value => new Rows(
                new Markup(
                    $"Net [bold]{Tables.Money(value.Net)}[/]  "
                    + $"owed to you [green]{value.OwedToYouTotal:N2}[/]  "
                    + $"you owe [red]{value.YouOweTotal:N2}[/]\n"),
                Side("You pay", value.YouPay),
                Side("Owed to you", value.OwedToYou)));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Records one payment between you and one person, wherever the debt between you lives.
    /// </summary>
    /// <remarks>
    /// The direction is stated rather than read off the balance, for the same reason
    /// <c>groups settle</c> states it: somebody recording "I paid you back" is acting
    /// precisely while the balance still says they owe.
    /// <para>
    /// The plan is read first, so the confirmation lists the groups the payment will land in
    /// rather than describing them. That list is the whole promise of the command: one
    /// payment, several transfers, one save.
    /// </para>
    /// </remarks>
    private static Command Pay()
    {
        var user = new Argument<Guid>("user")
        {
            Description = "The person on the other end of the payment."
        };

        var amount = Amounts.Option("--amount",
            "How much moved, e.g. 40.00. Defaults to the whole of what is outstanding between you.");

        var direction = new Option<SettlementDirection>("--direction")
        {
            Description = "Who paid whom.",
            DefaultValueFactory = _ => SettlementDirection.TheyPaidYou
        };

        var date = new Option<DateTimeOffset?>("--date")
        {
            Description = "When the money moved, e.g. 2026-09-30. Defaults to now."
        };

        var note = new Option<string?>("--note")
        {
            Description = "What to remember about it -- cash, a bank reference."
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would be recorded and stop."
        };

        var through = ThroughOption.Create(
            "Settle only what is owed for activity dated up to this day, e.g. 2026-08-31.");

        var command = new Command("pay", "Record one payment between you and one person.")
        {
            user, amount, direction, date, note, dryRun, through
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var userId = parse.GetValue(user);
            var chosen = parse.GetValue(direction);
            var day = parse.GetValue(through);
            var offset = ThroughOption.OffsetFor(day);

            // Through a day, what is outstanding -- and so the default amount and where it
            // lands -- is what is owed for that period after everything already paid.
            var plan = await context.Users.GetSettlementPlanAsync(day, offset, ct);

            var side = chosen is SettlementDirection.YouPaidThem ? plan.YouPay : plan.OwedToYou;
            var person = side.FirstOrDefault(entry => entry.UserId == userId);

            if (person is null)
            {
                // Not an error: being square with somebody is a perfectly good answer to
                // "settle with them", and a script that had to read a 409 to find that out
                // would treat squareness as a failure.
                context.Output.Write(
                    new { status = "nothing-outstanding", userId, direction = chosen.ToString() },
                    _ => new Markup("Nothing outstanding between the two of you in that direction.\n"));

                return ExitCodes.Success;
            }

            var paying = parse.GetValue(amount) ?? person.Amount;

            var lines = Allocation(paying, person.Groups)
                .Select(part => $"{part.GroupName}: {part.Amount:N2}")
                .ToList();

            if (parse.GetValue(dryRun))
            {
                context.Output.Write(
                    new { userId, person.UserName, amount = paying, groups = lines, through = day },
                    _ => new Rows(
                        new Markup($"[bold]{paying:N2}[/] to {Markup.Escape(person.UserName)}, "
                                   + $"across {lines.Count} {(lines.Count == 1 ? "group" : "groups")}.\n"),
                        Table("Group", lines)));

                return ExitCodes.Success;
            }

            Confirmation.Require(
                context,
                action: "settle.pay",
                summary: chosen is SettlementDirection.YouPaidThem
                    ? $"Record {paying:N2} paid to {person.UserName}?"
                    : $"Record {paying:N2} received from {person.UserName}?",
                changes: lines,
                // The figure the prompt named, not "whatever is outstanding": confirming
                // after another repayment landed must not pay the new remainder instead.
                // Written the one way --amount is read on every machine -- a '.' decimal
                // and two places -- so it comes back as the same number whatever the
                // culture; ConfirmCommandTests checks under es-ES.
                pinned: new Dictionary<Option, string>
                {
                    [amount] = paying.ToString("0.00", CultureInfo.InvariantCulture)
                });

            var recorded = await context.Users.SettleWithPersonAsync(
                new SettleWithPersonRequest
                {
                    UserId = userId,
                    Amount = paying,
                    Direction = chosen,
                    Date = parse.GetValue(date),
                    Description = parse.GetValue(note),
                    Through = day,
                    UtcOffset = offset
                },
                ct);

            context.Output.Write(recorded, value => new Markup(
                $"[green]Recorded[/] {value.Amount:N2} "
                + (value.Direction is SettlementDirection.YouPaidThem ? "to " : "from ")
                + $"{Markup.Escape(value.UserName)} across "
                + $"{value.Groups.Count} {(value.Groups.Count == 1 ? "group" : "groups")}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Every repayment you were party to, in any group. It answers "did I already pay
    /// this?", which is the question that stops people settling twice.
    /// </summary>
    private static Command History()
    {
        var page = new Option<int?>("--page") { Description = "Which page, from 1." };

        var pageSize = new Option<int?>("--page-size")
        {
            Description = $"Rows per page, up to {PageRequest.MaxPageSize}."
        };

        var command = new Command("history", "List the repayments you have made and received.")
        {
            page, pageSize
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var settlements = await context.Users.GetSettlementsAsync(
                page: parse.GetValue(page),
                pageSize: parse.GetValue(pageSize),
                cancellationToken: ct);

            context.Output.Write(settlements, value =>
            {
                var table = Tables.Grid("When", "What", "Group", "Amount");

                foreach (var settlement in value.Items)
                {
                    table.AddRow(
                        settlement.DateTime.ToLocalTime().ToString("yyyy-MM-dd"),
                        Markup.Escape((settlement.PaidByYou
                                          ? $"You paid {settlement.ToUserName}"
                                          : $"{settlement.FromUserName} paid you")
                                      + (settlement.CoversThrough is { } covers
                                          ? $" (through {covers:yyyy-MM-dd})"
                                          : string.Empty)),
                        Markup.Escape(settlement.GroupName),
                        $"{settlement.Amount:N2}");
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// One half of the plan as a table, or a line saying there is nothing on that side.
    /// </summary>
    private static IRenderable Side(string title, IReadOnlyList<PersonSettlement> people)
    {
        if (people.Count == 0)
            return new Markup($"[dim]{title}: nothing.[/]\n");

        var table = Tables.Grid(title, "Groups", "Amount");

        foreach (var person in people)
        {
            // A guest is one of the group's people with no account: the members record what
            // they pay and are paid, so they are in the plan like anybody else. Marked,
            // because the same real person can be a guest in two groups and is then two rows.
            table.AddRow(
                Markup.Escape(person.UserName) + (person.IsGuest ? " [yellow](guest)[/]" : string.Empty),
                Markup.Escape(string.Join(", ", person.Groups.Select(part =>
                    person.Groups.Count == 1 ? part.GroupName : $"{part.GroupName} {part.Amount:N2}"))),
                $"{person.Amount:N2}");
        }

        return table;
    }

    private static IRenderable Table(string header, IEnumerable<string> lines)
    {
        var table = Tables.Grid(header);

        foreach (var line in lines)
        {
            table.AddRow(Markup.Escape(line));
        }

        return table;
    }

    /// <summary>
    /// How the API will spread the payment: largest group first, until it runs out.
    /// </summary>
    /// <remarks>
    /// A second copy of the server's rule, and worth it: the confirmation this feeds is the
    /// last thing somebody sees before a write, and one that described the payment without
    /// saying where it lands would be asking them to agree to something they cannot check.
    /// The server remains the one that decides -- this only says what it will do.
    /// </remarks>
    private static IEnumerable<GroupDebt> Allocation(decimal amount, IReadOnlyList<GroupDebt> groups)
    {
        var remaining = amount;

        foreach (var part in groups.OrderByDescending(part => part.Amount))
        {
            if (remaining <= 0)
                yield break;

            var spent = Math.Min(remaining, part.Amount);
            remaining -= spent;

            yield return part with { Amount = spent };
        }
    }
}
