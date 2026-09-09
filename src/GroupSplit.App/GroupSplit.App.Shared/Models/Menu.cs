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

    /// <summary>
    /// Whether this item shows how many people are outstanding, in either direction.
    /// </summary>
    /// <remarks>
    /// A second flag rather than a shared one, because the two counts mean different things
    /// and only one of them is a queue. An inbox badge counts rows a bank sent that nobody
    /// has looked at; this one counts people somebody has to square up with, which is a
    /// standing state rather than something arriving.
    /// </remarks>
    public bool ShowsSettleCount { get; init; }
}