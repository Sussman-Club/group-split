using Bunit;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;
using MudBlazor.Services;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// What every component test needs standing up before it can render anything: MudBlazor's
/// own services, a JS runtime that answers whatever it is asked, and the three things the
/// app's components inject almost universally -- the error presenter, the clock and the
/// notifier.
/// </summary>
/// <remarks>
/// These are the first component tests in the repo. Until now the Blazor library had none
/// of any kind, which is why <c>tests/coverage.config</c> excludes it and why the defect
/// these were written against -- the group's summary cards keeping their all-time figures
/// while the list under them showed one month -- could not be caught by anything that ran.
/// <para>
/// The exclusion stays for now: a Razor component compiles into <c>obj/</c>, which the
/// coverage config drops as generated, so these move the measured figure hardly at all
/// while the library's other ~1,260 lines would arrive at once and take the build under
/// its floor. Taking it out is its own piece of work, and is still tracked in
/// <c>docs/qa/test-quality-and-coverage.md</c>.
/// </para>
/// </remarks>
public abstract class ComponentTest : BunitContext
{
    protected ComponentTest()
    {
        // Loose: these tests are about what a component asks the server for and what it
        // renders, and every JS call in the app is decoration -- the count-up animation,
        // the theme, the timezone probe. A strict runtime would only make each test
        // declare the decoration it does not care about.
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddMudServices();

        Services.AddSingleton(Snackbar.Object);
        Services.AddSingleton<ApiErrorPresenter>();
        Services.AddSingleton(new DataChangeNotifier());
        Services.AddSingleton<LoadGuard>();

        // A plain mock rather than bUnit's runtime, and deliberately not an in-process one:
        // the clock then cannot ask the browser for an offset and keeps the machine's, which
        // is what it does under a server-rendered host before the layout has asked. A test
        // that needs to know what day this clock thinks it is has to read it from here --
        // it is not necessarily the UTC day.
        Services.AddSingleton(new LocalClock(Mock.Of<IJSRuntime>()));

        Services.AddSingleton(Mock.Of<IAuthService>());

        // The production default. A test wanting a different answer registers its own after
        // this one, which is the point of the policy being a registration at all.
        Services.AddSingleton<IRemainderPolicy, LargestShareRemainderPolicy>();

        // Two dialogs say where an expense's shares came from, and the reader that answers
        // that joins a category to a rule to a version. Registered here, with clients that
        // answer "nothing", so a test about something else does not have to stand up three
        // services to render a dialog. A test that cares registers its own clients after
        // this constructor has run, and the later registration is the one resolved.
        Categories
            .Setup(client => client.GetCategoriesAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // A rule that has stood for one division since it was made, which is the smallest
        // thing the server can return: every rule has at least one version by construction,
        // and exactly one of them is open. A chain of none is a shape no run of the API
        // produces, and a test inheriting it would drive the reader into "that version
        // belongs to some other rule" through a response that cannot happen.
        SplitRules
            .Setup(client => client.GetSplitRuleVersionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                new SplitRuleHistoryResponse(id, Guid.Empty, "A split",
                [
                    new SplitRuleVersionResponse(
                        Guid.NewGuid(), DateTimeOffset.UtcNow.AddYears(-1), null, new EvenSplitRuleDto())
                ]));

        Services.AddSingleton(Categories.Object);
        Services.AddSingleton(SplitRules.Object);
        Services.AddSingleton<DivisionSourceReader>();
    }

    /// <summary>
    /// The categories client the <see cref="DivisionSourceReader"/> reads through, for a
    /// test that wants the strip above an expense's shares to say something.
    /// </summary>
    protected Mock<ICategoriesClient> Categories { get; } = new();

    protected Mock<ISplitRulesClient> SplitRules { get; } = new();

    /// <summary>Set up so a test can assert what a failure did or did not put in front of anybody.</summary>
    protected Mock<ISnackbar> Snackbar { get; } = new();

    protected DataChangeNotifier Changes => Services.GetRequiredService<DataChangeNotifier>();
}
