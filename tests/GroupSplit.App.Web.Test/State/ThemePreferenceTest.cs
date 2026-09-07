using GroupSplit.App.Shared.Services;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// The theme as the app resolves it, and what each way of changing it announces.
/// </summary>
/// <remarks>
/// These exist for the dark flash on the static-to-WebAssembly handover. The runtime
/// builds a fresh preference on takeover -- mode <see cref="ThemeMode.System"/>, no OS
/// answer yet -- and the browser's two answers used to be applied one at a time, with an
/// interop call in between. For a dark-desktop user who had chosen Light that left the
/// pair resolving to dark for the length of that call, which is long enough to paint;
/// the write-back that followed the same change also raced the read it came from and
/// could store "System" over the person's own choice. Hence one hydration step, and an
/// origin on the event so only a real choice is written back.
/// </remarks>
public class ThemePreferenceTest
{
    private static ThemePreference Preference(out List<ThemeChangeOrigin> changes)
    {
        var preference = new ThemePreference();
        var recorded = new List<ThemeChangeOrigin>();
        preference.Changed += recorded.Add;
        changes = recorded;
        return preference;
    }

    [Fact]
    public void Hydration_never_resolves_dark_on_the_way_to_a_chosen_light()
    {
        var preference = Preference(out var changes);
        var seen = new List<bool>();
        preference.Changed += _ => seen.Add(preference.IsDark);

        preference.Hydrate(ThemeMode.Light, systemIsDark: true);

        Assert.False(preference.IsDark);
        Assert.Equal(ThemeMode.Light, preference.Mode);

        // The flash was an announced dark in between. Whatever is announced here,
        // none of it may be dark.
        Assert.DoesNotContain(true, seen);
        Assert.All(changes, origin => Assert.Equal(ThemeChangeOrigin.Browser, origin));
    }

    [Fact]
    public void Hydration_is_announced_as_the_browser_talking_not_a_choice()
    {
        var preference = Preference(out var changes);

        preference.Hydrate(ThemeMode.Dark, systemIsDark: false);

        Assert.True(preference.IsDark);
        Assert.Equal([ThemeChangeOrigin.Browser], changes);
    }

    [Fact]
    public void Hydration_that_changes_nothing_says_nothing()
    {
        var preference = Preference(out var changes);

        preference.Hydrate(ThemeMode.System, systemIsDark: false);

        Assert.Empty(changes);
    }

    [Fact]
    public void Hydration_announces_a_mode_that_moved_even_when_the_colour_did_not()
    {
        var preference = Preference(out var changes);

        // Light on a light desktop: still light, but the menu has a thumb to move.
        preference.Hydrate(ThemeMode.Light, systemIsDark: false);

        Assert.False(preference.IsDark);
        Assert.Equal([ThemeChangeOrigin.Browser], changes);
    }

    [Fact]
    public void Picking_a_mode_is_a_choice_and_so_worth_storing()
    {
        var preference = Preference(out var changes);

        preference.Set(ThemeMode.Dark);

        Assert.Equal([ThemeChangeOrigin.Choice], changes);
    }

    [Fact]
    public void A_desktop_that_switches_is_the_browser_talking_not_a_choice()
    {
        var preference = Preference(out var changes);

        preference.SetSystemIsDark(true);

        Assert.True(preference.IsDark);
        Assert.Equal([ThemeChangeOrigin.Browser], changes);
    }

    [Fact]
    public void A_desktop_that_switches_under_a_chosen_mode_changes_nothing()
    {
        var preference = Preference(out var changes);
        preference.Hydrate(ThemeMode.Light, systemIsDark: false);
        changes.Clear();

        preference.SetSystemIsDark(true);

        Assert.False(preference.IsDark);
        Assert.Empty(changes);
    }
}
