using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

public static class CategoryCommands
{
    private static readonly Argument<Guid> CategoryId = new("category-id")
    {
        Description = "The category's id, as shown by `groupsplit categories list`."
    };

    public static Command Build()
    {
        var categories = new Command("categories", "Expense categories and their default split rules.");

        categories.Subcommands.Add(List());
        categories.Subcommands.Add(Create());
        categories.Subcommands.Add(Update());
        categories.Subcommands.Add(Delete());

        return categories;
    }

    private static Command List()
    {
        var group = new Option<Guid?>("--group") { Description = "Only categories in this group." };
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

        return list;
    }

    private static Command Create()
    {
        var name = new Argument<string>("name") { Description = "What this kind of expense is called." };
        var group = new Option<Guid>("--group")
        {
            Description = "Group the category belongs to.",
            Required = true
        };

        var rule = new Option<Guid?>("--rule")
        {
            Description = "Split rule an expense in this category is divided by. "
                          + "Omit to divide evenly."
        };

        var command = new Command("create", "Create a category.") { name, group, rule };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var category = await new Api.CategoriesClient(context.ApiHttpClient).CreateCategoryAsync(
                new CreateCategoryRequest
                {
                    GroupId = parse.GetValue(group),
                    Name = parse.GetValue(name)!,
                    DefaultSplitRuleId = parse.GetValue(rule)
                },
                ct);

            context.Output.Write(category, value => new Markup(
                $"[green]Created[/] {Markup.Escape(value.Name)} [grey]{value.Id}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// A whole replacement, because the endpoint is a PUT and its request requires the name.
    /// So anything not given is read back off the category first rather than being sent
    /// blank -- an update that says nothing about the name should not rename it to nothing.
    /// </summary>
    private static Command Update()
    {
        var name = new Option<string?>("--name") { Description = "Rename the category." };
        var rule = new Option<Guid?>("--rule") { Description = "Point it at this split rule." };
        var noRule = new Option<bool>("--no-rule")
        {
            Description = "Clear the default rule, so expenses in it divide evenly."
        };

        var command = new Command("update", "Change a category's name or its default rule.")
        {
            CategoryId, name, rule, noRule
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(CategoryId);

            // GetValue for the flag and GetResult for the id: a bool option always has a
            // result, so asking whether it was given has to mean asking whether it is true.
            if (parse.GetValue(noRule) && parse.GetResult(rule) is not null)
            {
                throw CliException.Input(
                    "--rule and --no-rule contradict each other.",
                    "Pass one or the other.");
            }

            var client = new Api.CategoriesClient(context.ApiHttpClient);

            // Read across the listing, since a category has no route of its own. A guid
            // that is not there fails here rather than as a PUT of a half-built request.
            var categories = await client.GetCategoriesAsync(null, ct);
            var current = categories.FirstOrDefault(candidate => candidate.Id == id);

            if (current is null)
            {
                throw new CliException(
                    new CliError(
                        "No category with that id.",
                        Shared.Errors.ErrorCodes.CategoryNotFound,
                        "List what you can see with: groupsplit categories list"),
                    ExitCodes.InvalidInput);
            }

            var updated = await client.UpdateCategoryAsync(
                id,
                new UpdateCategoryRequest
                {
                    Name = parse.GetValue(name) ?? current.Name,
                    DefaultSplitRuleId = parse.GetValue(noRule)
                        ? null
                        : parse.GetValue(rule) ?? current.DefaultSplitRuleId
                },
                ct);

            context.Output.Write(updated, value => new Markup(
                $"[green]Updated[/] {Markup.Escape(value.Name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Delete()
    {
        var command = new Command("delete", "Delete a category.") { CategoryId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(CategoryId);
            var client = new Api.CategoriesClient(context.ApiHttpClient);

            var categories = await client.GetCategoriesAsync(null, ct);
            var name = categories.FirstOrDefault(candidate => candidate.Id == id)?.Name ?? id.ToString();

            Confirmation.Require(
                context,
                action: "categories.delete",
                summary: $"Delete the category '{name}'?",
                changes:
                [
                    $"'{name}' stops being offered when an expense is recorded.",
                    "Refused if any expense is still filed under it -- a category cannot take "
                    + "the group's spending history with it."
                ],
                confirmCommand: $"groupsplit categories delete {id} --yes");

            await client.DeleteCategoryAsync(id, ct);

            context.Output.Write(
                new { status = "deleted", categoryId = id, name },
                value => new Markup($"[green]Deleted[/] {Markup.Escape(value.name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
