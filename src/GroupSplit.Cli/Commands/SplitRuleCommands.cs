using System.CommandLine;
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

    private static IRenderable Render(SplitRuleDetailsResponse rule)
    {
        var table = Tables.KeyValue();
        table.AddRow("Id", rule.Id.ToString());
        table.AddRow("Name", Markup.Escape(rule.Name));
        table.AddRow("Group", rule.GroupId.ToString());
        table.AddRow("Kind", Definitions.Describe(rule.Definition));

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

        var command = new Command("update", "Change a rule's name or how it divides.") { RuleId, name };
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
                    "Expenses already recorded keep the shares they were saved with."
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
