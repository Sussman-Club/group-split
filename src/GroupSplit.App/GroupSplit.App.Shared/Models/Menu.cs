using Microsoft.AspNetCore.Components.Routing;

namespace GroupSplit.App.Shared.Models;

public class MenuSection
{
    public string? Title { get; init; }
    public List<MenuItem> Items { get; } = new();
}

public class MenuItem
{
    public string Href { get; init; } = "";
    public string Text { get; init; } = "";
    public string Icon { get; init; } = "";
    public NavLinkMatch Match { get; init; } = NavLinkMatch.Prefix;

    /// <summary>
    /// Whether this item shows a count when there is one to show.
    /// </summary>
    /// <remarks>
    /// A flag rather than the number itself, because the list of items is static and shared
    /// and the number belongs to whoever is signed in. The nav asks for the count when it
    /// renders an item that wants one.
    /// </remarks>
    public bool ShowsInboxCount { get; init; }
}