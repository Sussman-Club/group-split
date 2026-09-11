using System.CommandLine;
using System.Text.Json;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

public static class SplitRuleCommands
{
    private static readonly Argument<Guid> RuleId = new("rule-id")
    {
        Description = "The rule's id, as shown by `groupsplit split-rules list`."
    };

    public static Command Build()
    {
        var rules = new Command("split-rules", "Reusable rules describing how an expense is divided.");

        rules.Subcommands.Add(List());
        rules.Subcommands.Add(Show());
        rules.Subcommands.Add(History());
        rules.Subcommands.Add(Create());
        rules.Subcommands.Add(Update());
        rules.Subcommands.Add(Delete());

        return rules;
    }

    private static Command List()
    {
        var group = new Option<Guid?>("--group") { Description = "Only rules in this group." };
        var list = new Command("list", "List split rules.") { group };

        list.SetHandler(async (context, ct) =>
        {
            var result = await new Api.SplitRulesClient(context.ApiHttpClient)
                .GetSplitRulesAsync(context.ParseResult.GetValue(group), ct);

            context.Output.Write(result, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("split rules") + "\n");
                }

                var table = Tables.Grid("Id", "Name");

                foreach (var rule in value)
                {
                    table.AddRow(rule.Id.ToString(), Markup.Escape(rule.Name));
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return list;
    }

    private static Command Show()
    {
        var command = new Command("show", "Show one rule and the division it stands for.") { RuleId };

        command.SetHandler(async (context, ct) =>
        {
            var rule = await new Api.SplitRulesClient(context.ApiHttpClient)
                .GetSplitRuleAsync(context.ParseResult.GetValue(RuleId), ct);

            context.Output.Write(rule, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Every division the rule has stood for, so an expense recorded months ago can be read
    /// against what the rule said then rather than against what it says now.
    /// </summary>
    private static Command History()
    {
        var command = new Command("versions", "Show every division a rule has stood for.") { RuleId };

        // A subcommand beside the argument, so `versions <rule-id>` still reads the history
        // and `versions set <rule-id>` writes one. Reading is what nearly everybody wants;
        // writing is a migration tool and has to be typed out.
        command.Subcommands.Add(SetHistory());

        command.SetHandler(async (context, ct) =>
        {
            var history = await new Api.SplitRulesClient(context.ApiHttpClient)
                .GetSplitRuleVersionsAsync(context.ParseResult.GetValue(RuleId), ct);

            context.Output.Write(history, Versions);

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Writes the divisions a rule stood for before it was recorded here, from a file.
    /// </summary>
    /// <remarks>
    /// From a file and not from flags, unlike every other way this CLI describes a division.
    /// A history is a list of them with a date each -- the owner's is twenty entries over 42
    /// months -- and there is no flag shape that says that without inventing one. The file is
    /// the wire format, so the same JSON goes in as goes out of <c>versions</c>.
    /// <para>
    /// It rewrites what the rule is recorded as having said, so it goes through the
    /// confirmation protocol like a deletion. <c>--dry-run</c> is the way to read the chain
    /// first, and it sends nothing at all -- not even the read the confirmation would need.
    /// </para>
    /// </remarks>
    private static Command SetHistory()
    {
        var file = new Option<FileInfo>("--file")
        {
            Description =
                "A JSON array of {\"from\": \"2023-03-01\", \"definition\": {\"$type\": \"shares\", ...}}, "
                + "oldest first. The definition is the same shape `split-rules show` prints.",
            Required = true
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Print the chain this would write and send nothing."
        };

        var command = new Command("set", "Write the divisions a rule stood for, from a file.")
        {
            RuleId, file, dryRun
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(RuleId);
            var entries = Read(parse.GetValue(file)!);

            if (parse.GetValue(dryRun))
            {
                context.Output.Write(
                    new { status = "dryRun", ruleId = id, versions = entries },
                    _ => new Rows(
                        new Markup("[grey]Dry run. Nothing was sent.[/]\n"),
                        Chain(entries)));

                return ExitCodes.Success;
            }

            Confirmation.Require(
                context,
                action: "split-rules.versions.set",
                summary: $"Rewrite the history of rule {id} as {entries.Count} versions?",
                changes:
                [
                    $"The rule is recorded as having said {entries.Count} different things, "
                    + $"the first from {entries[0].From:yyyy-MM-dd}.",
                    "The version it is on now stays the open one and is backdated to "
                    + $"{entries[^1].From:yyyy-MM-dd}, so expenses already divided by it keep pointing at it.",
                    "Refused outright if the rule has already changed since it was created.",
                    "No expense's shares are touched. Pointing expenses at the right version "
                    + "is: groupsplit transactions reattach --group <group-id>"
                ],
                confirmCommand:
                $"groupsplit split-rules versions set {id} --file {parse.GetValue(file)!.Name} --yes");

            var history = await new Api.SplitRulesClient(context.ApiHttpClient)
                .SetSplitRuleVersionsAsync(id, entries, ct);

            context.Output.Write(history, value => new Rows(
                new Markup(
                    $"[green]Wrote[/] {value.Versions.Count} versions of "
                    + $"{Markup.Escape(value.Name)}.\n"),
                Versions(value)));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// How a history file is read: the wire format, with one thing pinned down that the wire
    /// leaves to the machine.
    /// </summary>
    private static readonly JsonSerializerOptions HistoryFormat =
        new(GroupSplitSerializer.Options) { Converters = { new HistoryDate() } };

    /// <summary>
    /// Reads a <c>from</c> as the instant it names, and a bare date as midnight UTC.
    /// </summary>
    /// <remarks>
    /// System.Text.Json reads <c>"2023-03-01"</c> with whatever offset the machine is on, so
    /// the same file would open a version five hours apart in New York and in London. These
    /// dates are window boundaries and the windows decide which version an expense on the
    /// first of the month falls into, so the answer cannot depend on where the command was
    /// typed. A date carrying an offset of its own still means what it says.
    /// </remarks>
    private sealed class HistoryDate : System.Text.Json.Serialization.JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(
                reader.GetString() ?? string.Empty,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal);

        public override void Write(
            Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    /// <summary>
    /// The entries as the file states them, checked far enough that a typo is a usage error
    /// rather than a 400 from three hundred miles away.
    /// </summary>
    private static IReadOnlyList<SplitRuleVersionInput> Read(FileInfo file)
    {
        if (!file.Exists)
        {
            throw CliException.Input(
                $"No such file: {file.FullName}",
                "Pass --file with a path to a JSON array of {\"from\", \"definition\"} entries.");
        }

        List<SplitRuleVersionInput>? entries;

        try
        {
            entries = JsonSerializer.Deserialize<List<SplitRuleVersionInput>>(
                File.ReadAllText(file.FullName), HistoryFormat);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw CliException.Input(
                $"{file.Name} is not a history this can read: {exception.Message}",
                "Each entry is {\"from\": \"2023-03-01\", \"definition\": {\"$type\": \"even\"}}. "
                + "The kinds are even, payer, percent and shares.");
        }

        if (entries is not { Count: > 0 })
        {
            throw CliException.Input(
                $"{file.Name} describes no versions.",
                "A history needs at least one entry: the division the rule stands for now.");
        }

        return entries;
    }

    /// <summary>The chain a file describes, with each entry's window worked out from the next.</summary>
    private static IRenderable Chain(IReadOnlyList<SplitRuleVersionInput> entries)
    {
        var table = Tables.Grid("From", "Until", "Kind", "Division");

        for (var i = 0; i < entries.Count; i++)
        {
            var shares = Definitions.Shares(entries[i].Definition);

            table.AddRow(
                entries[i].From.ToString("u"),
                i == entries.Count - 1 ? "now" : entries[i + 1].From.ToString("u"),
                Definitions.Describe(entries[i].Definition),
                Markup.Escape(shares.Count == 0
                    ? "-"
                    : string.Join(", ", shares.Select(share => $"{share.UserId}: {share.Share}"))));
        }

        return table;
    }

    /// <summary>A rule's history as the server reports it back, newest first.</summary>
    private static IRenderable Versions(SplitRuleHistoryResponse history)
    {
        var table = Tables.Grid("Version", "From", "Until", "Kind", "Division");

        foreach (var version in history.Versions)
        {
            var shares = Definitions.Shares(version.Definition);

            table.AddRow(
                version.Id.ToString(),
                version.StartedAt.ToString("u"),
                version.SupersededAt?.ToString("u") ?? "now",
                Definitions.Describe(version.Definition),
                Markup.Escape(shares.Count == 0
                    ? "-"
                    : string.Join(", ", shares.Select(share => $"{share.UserId}: {share.Share}"))));
        }

        return table;
    }

    private static IRenderable Render(SplitRuleDetailsResponse rule)
    {
        var table = Tables.KeyValue();
        table.AddRow("Id", rule.Id.ToString());
        table.AddRow("Name", Markup.Escape(rule.Name));
        table.AddRow("Group", rule.GroupId.ToString());
        table.AddRow("Kind", Definitions.Describe(rule.Definition));
        table.AddRow("Version", rule.VersionId.ToString());
        table.AddRow("Changed", rule.ChangedAt.ToString("u"));

        var shares = Definitions.Shares(rule.Definition);

        if (shares.Count == 0)
        {
            return table;
        }

        var detail = Tables.Grid("Member", "Share");

        foreach (var (userId, share) in shares)
        {
            detail.AddRow(userId.ToString(), Markup.Escape(share));
        }

        return new Rows(table, new Markup("\n[bold]Division[/]\n"), detail);
    }

    private static Command Create()
    {
        var name = new Argument<string>("name") { Description = "What to call the rule." };
        var group = new Option<Guid>("--group")
        {
            Description = "Group the rule belongs to.",
            Required = true
        };

        var kind = new DefinitionOptions();

        var command = new Command("create", "Create a split rule.") { name, group };
        kind.AddTo(command);

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var rule = await new Api.SplitRulesClient(context.ApiHttpClient).CreateSplitRuleAsync(
                new CreateSplitRuleRequest
                {
                    GroupId = parse.GetValue(group),
                    Name = parse.GetValue(name)!,
                    // Required here, unlike on update: there is nothing to read a division
                    // back off, and a rule that divides nothing is not a rule.
                    Definition = kind.Read(parse, current: null)
                              ?? throw CliException.Input(
                                  "A rule needs a division.",
                                  "Pass one of --even, --payer, --percent or --shares.")
                },
                ct);

            context.Output.Write(rule, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The endpoint is a PUT and its request requires both the name and the division, so
    /// whichever of the two is not given is read back off the rule first. Sending a blank
    /// one would clear it, which is not what "change the name" means.
    /// </summary>
    private static Command Update()
    {
        var name = new Option<string?>("--name") { Description = "Rename the rule." };
        var kind = new DefinitionOptions();

        var command = new Command("update", "Rename a rule, or change how it divides from now on.")
        {
            RuleId, name
        };
        kind.AddTo(command);

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(RuleId);
            var client = new Api.SplitRulesClient(context.ApiHttpClient);

            var current = await client.GetSplitRuleAsync(id, ct);

            var updated = await client.UpdateSplitRuleAsync(
                id,
                new UpdateSplitRuleRequest
                {
                    Name = parse.GetValue(name) ?? current.Name,
                    Definition = kind.Read(parse, current.Definition)!
                },
                ct);

            context.Output.Write(updated, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Delete()
    {
        var command = new Command("delete", "Delete a split rule.") { RuleId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(RuleId);
            var client = new Api.SplitRulesClient(context.ApiHttpClient);

            var rule = await client.GetSplitRuleAsync(id, ct);

            Confirmation.Require(
                context,
                action: "split-rules.delete",
                summary: $"Delete the rule '{rule.Name}'?",
                changes:
                [
                    $"'{rule.Name}' stops being available to the group's categories.",
                    "Every version of it is deleted with it.",
                    "Refused outright if any expense was divided by one of those versions."
                ],
                confirmCommand: $"groupsplit split-rules delete {id} --yes");

            await client.DeleteSplitRuleAsync(id, ct);

            context.Output.Write(
                new { status = "deleted", ruleId = id, name = rule.Name },
                value => new Markup($"[green]Deleted[/] {Markup.Escape(value.name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
