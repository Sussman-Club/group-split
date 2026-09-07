using System.CommandLine;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>Which way a sorted listing runs.</summary>
public enum SortOrder
{
    Asc,
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
    public static Option<SortOrder?> Order() => new("--order")
    {
        Description = "asc or desc. Defaults to whichever way the sort key reads naturally."
    };

    public static bool? Descending(this SortOrder? order) => order switch
    {
        SortOrder.Asc => false,
        SortOrder.Desc => true,
        _ => null
    };
}
