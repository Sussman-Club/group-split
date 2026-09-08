using GroupSplit.AppHost.Test.Base;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace GroupSplit.AppHost.Test.Web;

// See the note in HomePageTest: Playwright's API takes no CancellationToken, so a test
// carrying a Timeout has nothing to hand TestContext.Current.CancellationToken to.
#pragma warning disable xUnit1069

/// <summary>
/// The count-up in <c>gs.js</c>, against the one thing it shares with Blazor: the text
/// node holding the figure.
/// </summary>
/// <remarks>
/// Every amount on a page is a <c>&lt;Money&gt;</c>, and an animated one is a span whose
/// text Blazor renders and whose digits this script then counts up to. Blazor keeps a
/// reference to the text node it created and writes each later figure straight into that
/// node, so the script may only ever change the node's data -- never assign
/// <c>element.textContent</c>, which discards the node and inserts a new one. It did
/// assign it, and so the first count an element ran was the last figure it ever showed:
/// afterwards Blazor's writes went to a node no longer in the document, while the
/// attribute, the caption and everything around it kept moving. A whole page of stale
/// totals under fresh captions, and no test in either suite could see it, because bUnit
/// renders no browser and this script never runs there.
/// <para>
/// So this drives the script itself rather than a page: a blank document, the real file,
/// and the two writes Blazor makes for one figure. Nothing here needs the app, but the
/// AppHost fixture is this project's, and this is the project with a browser.
/// </para>
/// </remarks>
public class CountUpTest : PageTest
{
    private const float OperationTimeoutMs = 30_000;

    /// <summary>
    /// The script is copied beside the test binary by the project file, so this reads the
    /// file the app ships rather than a copy of it that could drift.
    /// </summary>
    private static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "gs.js");

    /// <summary>
    /// The count only runs for a reader who has not asked for less motion, and a run is
    /// the whole subject here -- under <c>reduce</c> the script returns before it writes
    /// anything and the defect cannot appear. Stated rather than inherited, so the test
    /// does not quietly pass because of how a machine is configured.
    /// </summary>
    public override BrowserNewContextOptions ContextOptions()
    {
        var options = base.ContextOptions();
        options.ReducedMotion = ReducedMotion.NoPreference;
        return options;
    }

    /// <summary>
    /// Renders an amount the way <c>Money</c> does, and hands back the handle Blazor would
    /// hold: the text node, captured as the element goes in and before any frame can run.
    /// </summary>
    private async Task RenderAmountAsync(string figure, string text)
    {
        await Page.SetContentAsync("<body></body>");
        await Page.AddScriptTagAsync(new PageAddScriptTagOptions { Path = ScriptPath });

        await Page.EvaluateAsync(
            """
            ([figure, text]) => {
                const el = document.createElement("span");
                el.className = "gs-money";
                el.setAttribute("data-gs-count", figure);
                el.textContent = text;

                document.body.appendChild(el);

                window.el = el;
                window.rendered = el.firstChild;
            }
            """,
            new[] { figure, text });
    }

    /// <summary>
    /// The next render, as Blazor makes it: the attribute set on the element, and the text
    /// written into the node it is holding on to.
    /// </summary>
    private Task RenderAgainAsync(string figure, string text) => Page.EvaluateAsync(
        """
        ([figure, text]) => {
            window.el.setAttribute("data-gs-count", figure);
            window.rendered.data = text;
        }
        """,
        new[] { figure, text });

    /// <summary>Long enough for a run to finish and land on its final text.</summary>
    private Task SettleAsync() => Page.WaitForTimeoutAsync(1_500);

    /// <summary>
    /// The defect, as the page showed it: a figure counted once, then a new one rendered
    /// over it, and the new one has to be the one on screen.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_figure_rendered_after_a_count_has_run_is_the_one_on_screen()
    {
        await RenderAmountAsync("220.50", "$220.50");
        await SettleAsync();

        await RenderAgainAsync("366.59", "$366.59");

        await Expect(Page.Locator(".gs-money")).ToHaveTextAsync(
            "$366.59", new LocatorAssertionsToHaveTextOptions { Timeout = OperationTimeoutMs });
    }

    /// <summary>
    /// The same again and again, because the defect only began at the first count: the
    /// element survived one render and froze on the next.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task It_keeps_following_the_data_across_repeated_renders()
    {
        await RenderAmountAsync("220.50", "$220.50");
        await SettleAsync();

        foreach (var (figure, text) in new[] { ("366.59", "$366.59"), ("0", "$0.00"), ("17.43", "$17.43") })
        {
            await RenderAgainAsync(figure, text);
            await SettleAsync();

            await Expect(Page.Locator(".gs-money")).ToHaveTextAsync(
                text, new LocatorAssertionsToHaveTextOptions { Timeout = OperationTimeoutMs });
        }
    }

    /// <summary>
    /// The mechanism under the two assertions above. Blazor addresses that node directly
    /// on every later render, so the node surviving is the whole of what keeps the two in
    /// step -- and it is what an edit here could take away again without any figure
    /// looking wrong until a second one is rendered.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_text_node_the_app_rendered_is_still_the_one_in_the_document()
    {
        await RenderAmountAsync("220.50", "$220.50");
        await SettleAsync();

        var same = await Page.EvaluateAsync<bool>(
            "() => window.rendered === window.el.firstChild && window.rendered.isConnected");

        Assert.True(same, "the count-up replaced the text node the app rendered");
    }

    /// <summary>
    /// And the count still ends on the figure it was given, which is the reason any of
    /// this writing happens at all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_count_lands_on_the_figure_it_was_counting_towards()
    {
        await RenderAmountAsync("1284.50", "$1,284.50");
        await SettleAsync();

        await Expect(Page.Locator(".gs-money")).ToHaveTextAsync(
            "$1,284.50", new LocatorAssertionsToHaveTextOptions { Timeout = OperationTimeoutMs });
    }
}
