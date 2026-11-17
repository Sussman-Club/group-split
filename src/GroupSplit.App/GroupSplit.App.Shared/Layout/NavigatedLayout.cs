using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace GroupSplit.App.Shared.Layout;

/// <summary>
/// A layout that knows whether the page it wraps arrived by navigation or by hydration,
/// and lets only the first of those animate in.
/// </summary>
/// <remarks>
/// The page is server-rendered first and the interactive runtime takes over later. On
/// that takeover blazor.web.js empties the prerendered markup and inserts the runtime's
/// own first render in its place, so every node on the page is new -- and a CSS entrance
/// animation runs on a new node. The whole page therefore faded in a second time, from
/// nothing, over a page that was already there: the blink on the way from the static
/// page to the interactive one. There is no switch in Blazor for keeping the
/// prerendered nodes, so the fix is on this side: the animations are attached to
/// <see cref="EnterClass"/>, and this class is only ever put on a wrapper created after
/// the person has navigated. The prerender and the render that replaces it carry no
/// class, and so nothing to animate.
/// <para>
/// The class is tied to the wrapper's key, not to a flag on its own. Turning a selector
/// on for an element already in the document starts its animation just the same as
/// creating it, which is why a query-string change -- the same page asking a different
/// question, with the same key -- must not count as a navigation here: it would have put
/// the class on the wrapper the person was looking at and faded it out under them.
/// </para>
/// </remarks>
public abstract class NavigatedLayout : LayoutComponentBase, IDisposable
{
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    /// <summary>
    /// The address without its query string or fragment: the part that says which page.
    /// </summary>
    /// <remarks>
    /// The key used to be the whole address, and a page that keeps a filter in its query
    /// string -- the You page's view, the ledger's kind -- was torn down and built again
    /// every time that filter moved. The date range and the search box went with it, and
    /// the instance that had asked the server for the new figures was disposed before they
    /// arrived. A change of query is the same page answering a different question, not a
    /// navigation.
    /// </remarks>
    protected string Path { get; private set; } = "";

    /// <summary>
    /// True once the person has moved to another page inside the interactive app; false
    /// for the page they loaded, whether prerendered or hydrated.
    /// </summary>
    protected bool Navigated { get; private set; }

    /// <summary>
    /// The class that turns entrance animations on. Empty for the page the person loaded,
    /// so the runtime's takeover changes nothing they can see.
    /// </summary>
    protected string EnterClass => Navigated ? "gs-page-enter" : "";

    protected override void OnInitialized()
    {
        Path = PathOf(Nav.Uri);
        Nav.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var path = PathOf(e.Location);

        // Only a new page is a navigation. The key moves with the path, so the wrapper
        // that picks up the class is always a new one.
        if (path != Path)
        {
            Path = path;
            Navigated = true;
        }

        InvokeAsync(StateHasChanged);
    }

    private static string PathOf(string uri)
    {
        var cut = uri.IndexOfAny(['?', '#']);

        return cut < 0 ? uri : uri[..cut];
    }

    public virtual void Dispose() => Nav.LocationChanged -= OnLocationChanged;
}
