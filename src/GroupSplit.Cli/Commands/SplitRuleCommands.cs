using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

public static class SplitRuleCommands
{
    public static Command Build()
    {
        var group = new Option<Guid?>("--group") { Description = "Only rules in this group." };
        var rules = new Command("split-rules", "Reusable rules describing how an expense is divided.");
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

        rules.Subcommands.Add(list);

        return rules;
    }
}
