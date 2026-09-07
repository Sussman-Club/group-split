using System.CommandLine;
using System.CommandLine.Completions;
using System.ComponentModel;
using System.Reflection;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Gives the values of an enum-valued option or argument the same treatment every command
/// and option already gets: a description beside each one in the shell.
/// <para>
/// System.CommandLine completes an enum from its member names and stops there. The
/// <see cref="CompletionItem"/> it builds for a value carries no <c>Detail</c>, because
/// there is nowhere on an enum for the parser to read one from -- so <c>-o &lt;TAB&gt;</c>
/// offers a bare Auto/Json/Text while every command beside it is described. This reads
/// <see cref="DescriptionAttribute"/> off the members instead, which keeps the wording on
/// the enum where the values are declared rather than at each use site.
/// </para>
/// </summary>
public static class ValueCompletions
{
    public static Option<TValue> WithDescribedValues<TValue>(this Option<TValue> option)
        where TValue : struct, Enum
    {
        Replace(option.CompletionSources, Describe<TValue>());
        return option;
    }

    /// <summary>
    /// The same, for an option whose absence means something -- a filter nobody set, a sort
    /// direction left to the server. The values a shell offers are the enum's either way;
    /// only the type it binds to differs.
    /// </summary>
    public static Option<TValue?> WithDescribedValues<TValue>(this Option<TValue?> option)
        where TValue : struct, Enum
    {
        Replace(option.CompletionSources, Describe<TValue>());
        return option;
    }

    public static Argument<TValue> WithDescribedValues<TValue>(this Argument<TValue> argument)
        where TValue : struct, Enum
    {
        Replace(argument.CompletionSources, Describe<TValue>());
        return argument;
    }

    /// <summary>
    /// Cleared rather than appended to: the framework's own source is already in the list,
    /// and leaving it would offer every value a second time with nothing beside it.
    /// </summary>
    private static void Replace(
        List<Func<CompletionContext, IEnumerable<CompletionItem>>> sources,
        IReadOnlyList<CompletionItem> described)
    {
        sources.Clear();
        sources.Add(_ => described);
    }

    private static IReadOnlyList<CompletionItem> Describe<TValue>() where TValue : struct, Enum =>
        typeof(TValue)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(value => new CompletionItem(
                value.Name,
                kind: "Value",
                sortText: value.Name,
                detail: value.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .ToList();
}
