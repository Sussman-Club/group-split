using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class TimeZonePickerTest : ComponentTest
{
    [Fact]
    public async Task Search_suggests_matching_cities_and_keeps_the_selected_zone()
    {
        JSInterop.Setup<string?>("localStorage.getItem", "groupsplit.timeZone").SetResult("Europe/London");
        JSInterop.Setup<string[]>("gs.timeZones").SetResult(["Europe/London", "America/New_York", "Asia/Tokyo"]);
        JSInterop.Setup<string?>("gs.timeFormat").SetResult("system");
        var picker = Render<TimeZonePicker>();
        var autocomplete = picker.FindComponent<MudAutocomplete<string>>();
        var options = await autocomplete.Instance.SearchFunc!("new york", CancellationToken.None);
        Assert.Contains("America/New_York", options);
        Assert.DoesNotContain("Europe/London", options);
        Assert.DoesNotContain("Asia/Tokyo", options);
        Assert.Equal("Europe/London", autocomplete.Instance.Value);
        Assert.Empty(await autocomplete.Instance.SearchFunc!("missing", CancellationToken.None));
    }

    [Fact]
    public async Task Choosing_a_zone_updates_the_clock_and_saves_the_choice()
    {
        JSInterop.Setup<string?>("localStorage.getItem", "groupsplit.timeZone").SetResult(null);
        JSInterop.Setup<string[]>("gs.timeZones").SetResult(["America/New_York"]);
        JSInterop.Setup<string?>("gs.timeFormat").SetResult("system");
        JSInterop.Setup<string?>("gs.setTimeZone", "America/New_York").SetResult("America/New_York");
        var picker = Render<TimeZonePicker>();
        var autocomplete = picker.FindComponent<MudAutocomplete<string>>();
        await picker.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync("America/New_York"));
        var clock = Services.GetRequiredService<LocalClock>();
        Assert.Equal(8, clock.Local(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)).Hour);
        JSInterop.VerifyInvoke("gs.setTimeZone");
    }

    [Fact]
    public void Choosing_a_time_format_updates_the_clock_and_saves_the_choice()
    {
        JSInterop.Setup<string?>("localStorage.getItem", "groupsplit.timeZone").SetResult(null);
        JSInterop.Setup<string[]>("gs.timeZones").SetResult([]);
        JSInterop.Setup<string?>("gs.timeFormat").SetResult("system");
        JSInterop.SetupVoid("gs.setTimeFormat", "12");
        var picker = Render<TimeZonePicker>();
        var buttons = picker.FindAll("[role=radio]");
        Assert.Equal("true", buttons[0].GetAttribute("aria-checked"));
        buttons[1].Click();
        buttons = picker.FindAll("[role=radio]");
        Assert.Equal("true", buttons[1].GetAttribute("aria-checked"));
        Assert.Equal("false", buttons[0].GetAttribute("aria-checked"));
        Assert.Equal(new TimeOnly(13, 5).ToString("h:mm tt"), Services.GetRequiredService<LocalClock>().FormatTime(new TimeOnly(13, 5), "HH:mm"));
        JSInterop.VerifyInvoke("gs.setTimeFormat");
    }
}
