using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace GroupSplit.AppHost.Test.Web;

// xUnit1069: as in HomePageTest -- Playwright's API takes no CancellationToken, so there is
// nothing here to hand TestContext.Current.CancellationToken to.
#pragma warning disable xUnit1069

/// <summary>
/// The count-up in <c>wwwroot/gs.js</c>: a figure marked <c>gs-figure</c> rises from zero
/// to the figure it is showing, and lands on exactly what the app rendered.
/// </summary>
/// <remarks>
/// The count writes into an element Blazor owns, which is what made issue #193 possible, so
/// what it writes is worth pinning. It is safe now because the figure is read from the
/// element's own text and a figure never changes in place -- the components key the element
/// on the value, so a changed figure is a new element. The old version took its target from
/// a <c>data-gs-count</c> attribute instead, which made one figure two edits to one element;
/// when they landed in the wrong order a run read the previous figure as the text to land on
/// and pasted it back, seconds later, over the figure the app had since rendered.
/// <para>
/// Nothing in the .NET suites can see any of this: the component tests run with a loose JS
/// runtime, so the script never executes. The previous fix was proven once by hand in a
/// browser and nothing ran afterwards to keep it proven, which is why the issue was reopened
/// against a build that happened not to contain it. These are that proof, run.
/// </para>
/// <para>
/// The clock and the frame pump are stubbed before the script loads, so each frame is
/// stepped deliberately rather than waited for. That makes the assertions exact instead of
/// timing-dependent, and it keeps the test working where <c>requestAnimationFrame</c> does
/// not run at all -- a headless or backgrounded page, which is most of CI.
/// </para>
/// <para>
/// No <c>AppHostFixture</c> is used: this needs the script and a DOM, not the app or the
/// network. It lives in this project because this is the repo's only browser suite, and CI
/// already pays for the stack here. The script is copied beside the test assembly by the
/// project file, so what runs is the file the app serves.
/// </para>
/// </remarks>
public class CountUpScriptTest : PageTest
{
    private const float OperationTimeoutMs = 30_000;

    /// <summary>What the script counts for; longer than a run, to settle one.</summary>
    private const int PastTheEnd = 2_000;

    /// <summary>
    /// A page holding the real script over a stubbed clock and frame pump, with nothing
    /// rendered yet -- so every figure below arrives as an insertion the script observes.
    /// </summary>
    private async Task OpenAsync()
    {
        await Page.SetContentAsync(
            """<div id="host"></div>""",
            new PageSetContentOptions { Timeout = OperationTimeoutMs });

        await Page.EvaluateAsync(
            """
            () => {
                window.__now = 0;
                performance.now = () => window.__now;

                window.__pending = [];
                window.requestAnimationFrame = cb => window.__pending.push(cb);

                // Advance the clock and drain exactly one generation of frames.
                window.__step = ms => {
                    window.__now += ms;
                    const due = window.__pending;
                    window.__pending = [];
                    for (const cb of due) cb(window.__now);
                };

                // Blazor renders a figure by inserting an element that already holds its
                // final text, and a changed figure by replacing that element -- the
                // components key it on the value.
                window.__render = (id, text, cls) => {
                    const el = document.createElement("span");
                    el.id = id;
                    el.className = cls === undefined ? "gs-figure" : cls;
                    el.textContent = text;
                    const existing = document.getElementById(id);
                    if (existing) existing.replaceWith(el);
                    else document.getElementById("host").appendChild(el);
                };
            }
            """);

        await Page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Path = Path.Combine(AppContext.BaseDirectory, "gs.js")
        });
    }

    /// <summary>Renders a figure and lets the script's observer deliver the insertion.</summary>
    private async Task RenderAsync(string id, string text, string? cssClass = null)
    {
        await Page.EvaluateAsync(
            """([id, text, cls]) => window.__render(id, text, cls === "" ? undefined : cls)""",
            new[] { id, text, cssClass ?? "" });

        // The MutationObserver delivers on a microtask, so the count has not started yet.
        await Page.EvaluateAsync("() => new Promise(r => setTimeout(r, 0))");
    }

    private Task StepAsync(int milliseconds) =>
        Page.EvaluateAsync("ms => window.__step(ms)", milliseconds);

    private Task<string> TextAsync(string id) =>
        Page.EvaluateAsync<string>("id => document.getElementById(id).textContent", id);

    /// <summary>Every frame of a run, from zero to the end.</summary>
    private async Task<IReadOnlyList<string>> FramesAsync(string id, int stepMs, int steps)
    {
        var frames = new List<string>();

        await StepAsync(0);
        frames.Add(await TextAsync(id));

        for (var i = 0; i < steps; i++)
        {
            await StepAsync(stepMs);
            frames.Add(await TextAsync(id));
        }

        return frames;
    }

    [Fact(Timeout = 120_000)]
    public async Task An_amount_rises_from_zero_to_the_figure_it_was_rendered_with()
    {
        await OpenAsync();
        await RenderAsync("m", "$1,234.56");

        var frames = await FramesAsync("m", 50, 12);

        Assert.Equal("$0.00", frames[0]);
        Assert.True(frames.Count > 2, "the count produced no frames");

        // Monotonic: a figure that dips on its way up reads as a glitch, not a count.
        var values = frames.Select(Amount).ToList();
        Assert.Equal(values.OrderBy(value => value), values);

        await StepAsync(PastTheEnd);
        Assert.Equal("$1,234.56", await TextAsync("m"));
    }

    /// <summary>
    /// The count formats itself the way the figure is formatted, on every frame -- the
    /// currency, both decimals, and a thousands separator only once there are thousands.
    /// A bare "1234.5" flashing inside a money card is the animation showing its working.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task An_amount_is_money_on_every_frame_and_not_only_at_the_end()
    {
        await OpenAsync();
        await RenderAsync("m", "$1,234.56");

        var frames = await FramesAsync("m", 50, 12);

        Assert.All(frames, frame => Assert.Matches(@"^\$[\d,]+\.\d{2}$", frame));

        Assert.All(frames, frame =>
            Assert.True(!frame.Contains(',') || Amount(frame) >= 1000,
                $"'{frame}' carries a thousands separator below a thousand"));
    }

    [Fact(Timeout = 120_000)]
    public async Task A_count_rises_in_whole_numbers()
    {
        await OpenAsync();
        await RenderAsync("c", "42");

        var frames = await FramesAsync("c", 80, 8);

        Assert.Equal("0", frames[0]);
        Assert.All(frames, frame => Assert.Matches(@"^\d+$", frame));

        await StepAsync(PastTheEnd);
        Assert.Equal("42", await TextAsync("c"));
    }

    /// <summary>
    /// Issue #193's shape, as far as it can still be expressed. A figure changes while its
    /// count is part-way up: the element is replaced, the new one counts from zero to the
    /// new figure, and nothing the abandoned run does afterwards -- its remaining frames or
    /// its settle backstop -- puts the old figure back.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_figure_that_changes_mid_count_leaves_the_new_one_showing()
    {
        await OpenAsync();
        await RenderAsync("s", "$70.00");

        await StepAsync(0);
        await StepAsync(200);
        var midway = await TextAsync("s");
        Assert.NotEqual("$70.00", midway);

        // The chip was clicked: a new figure, so a new element.
        await RenderAsync("s", "$10.00");

        await StepAsync(0);
        Assert.Equal("$0.00", await TextAsync("s"));

        await StepAsync(PastTheEnd);
        Assert.Equal("$10.00", await TextAsync("s"));

        // Drain whatever the abandoned run has left, including its real-time backstop.
        await StepAsync(PastTheEnd);
        await Page.WaitForTimeoutAsync(1_200);

        Assert.Equal("$10.00", await TextAsync("s"));
    }

    /// <summary>
    /// A negative is rendered in brackets by the pinned en-US currency format, and a
    /// positive under <c>Signed</c> carries a "+". The count runs inside whatever surrounds
    /// the digits rather than replacing it.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("($5.00)", @"^\(\$[\d,]+\.\d{2}\)$")]
    [InlineData("+$8.00", @"^\+\$[\d,]+\.\d{2}$")]
    public async Task A_sign_survives_the_count(string figure, string shape)
    {
        await OpenAsync();
        await RenderAsync("n", figure);

        var frames = await FramesAsync("n", 100, 5);

        Assert.All(frames, frame => Assert.Matches(shape, frame));

        await StepAsync(PastTheEnd);
        Assert.Equal(figure, await TextAsync("n"));
    }

    /// <summary>
    /// What the script leaves alone: a figure already at zero, text that is not a figure,
    /// and every amount not marked for a count -- which is most of them, the whole expenses
    /// grid included.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("$0.00", null)]
    [InlineData("Personal", null)]
    [InlineData("$99.00", "gs-money")]
    public async Task What_is_not_counted_is_left_exactly_as_rendered(string figure, string? cssClass)
    {
        await OpenAsync();
        await RenderAsync("x", figure, cssClass);

        await StepAsync(0);
        Assert.Equal(figure, await TextAsync("x"));

        await StepAsync(PastTheEnd);
        Assert.Equal(figure, await TextAsync("x"));
    }

    private static double Amount(string figure) =>
        double.Parse(figure.Replace("$", "").Replace(",", "").Replace("(", "").Replace(")", "")
                .Replace("+", ""),
            System.Globalization.CultureInfo.InvariantCulture);
}
