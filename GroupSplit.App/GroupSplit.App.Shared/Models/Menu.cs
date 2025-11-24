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
}