using System.CommandLine;
using System.ComponentModel;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Which way a sorted listing runs. The member descriptions are user-facing: a shell shows
/// them beside each value when completing <c>--order</c>.
/// </summary>
public enum SortOrder
{
    [Description("Smallest, earliest or first alphabetically at the top.")]
    Asc,

    [Description("Largest, most recent or last alphabetically at the top.")]
    Desc
}

/// <summary>
/// The <c>--order</c> flag every paged listing shares.
/// </summary>
/// <remarks>
/// A three-state option rather than a <c>--descending</c> flag, because the server's
/// default already differs per sort key -- dates and amounts arrive newest and largest
/// first, names alphabetically -- so "not descending" is not the same answer as "ascending",
/// and a boolean flag has no way to say the second one.
/// </remarks>
public static class Sorting
{
    public static Option<SortOrder?> Order() => new Option<SortOrder?>("--order")
    {
        Description = "asc or desc. Defaults to whichever way the sort key reads naturally."
    }.WithDescribedValues();

    public static bool? Descending(this SortOrder? order) => order switch
    {
        SortOrder.Asc => false,
        SortOrder.Desc => true,
        _ => null
    };
}
