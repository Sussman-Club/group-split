using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

public static class CategoryCommands
{
    public static Command Build()
    {
        var group = new Option<Guid?>("--group") { Description = "Only categories in this group." };
        var categories = new Command("categories", "Expense categories and their default split rules.");
        var list = new Command("list", "List categories.") { group };

        list.SetHandler(async (context, ct) =>
        {
            var result = await new Api.CategoriesClient(context.ApiHttpClient)
                .GetCategoriesAsync(context.ParseResult.GetValue(group), ct);

            context.Output.Write(result, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("categories") + "\n");
                }

                var table = Tables.Grid("Id", "Name", "Default rule");

                foreach (var category in value)
                {
                    table.AddRow(
                        category.Id.ToString(),
                        Markup.Escape(category.Name),
                        Markup.Escape(category.DefaultSplitRuleName ?? "-"));
                }

                return table;
            });

            return ExitCodes.Success;
        });

        categories.Subcommands.Add(list);

        return categories;
    }
}
