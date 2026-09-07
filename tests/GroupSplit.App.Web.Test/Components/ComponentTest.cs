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
        // renders, and every JS call in the app is decoration -- the count-up, the theme,
        // the timezone probe. A strict runtime would only make each test declare the
        // decoration it does not care about. The count-up's own behaviour needs a browser
        // and has one, in GroupSplit.AppHost.Test's CountUpScriptTest.
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
    }

    /// <summary>Set up so a test can assert what a failure did or did not put in front of anybody.</summary>
    protected Mock<ISnackbar> Snackbar { get; } = new();

    protected DataChangeNotifier Changes => Services.GetRequiredService<DataChangeNotifier>();
}
