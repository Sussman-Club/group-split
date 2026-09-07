using Bunit;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Errors;
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

        // Answers 0 from the loose JS runtime, so "today" is the UTC day and every test
        // here resolves its spans the same way wherever it runs.
        Services.AddSingleton(new LocalClock(Mock.Of<IJSRuntime>()));

        Services.AddSingleton(Mock.Of<IAuthService>());
    }

    /// <summary>Set up so a test can assert what a failure did or did not put in front of anybody.</summary>
    protected Mock<ISnackbar> Snackbar { get; } = new();

    protected DataChangeNotifier Changes => Services.GetRequiredService<DataChangeNotifier>();
}
