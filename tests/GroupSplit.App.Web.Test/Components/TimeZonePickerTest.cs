using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.App.Web.Test.Components;

public class TimeZonePickerTest : ComponentTest
{
    [Fact]
    public void Search_filters_cities_without_losing_the_selected_zone()
    {
        JSInterop.Setup<string?>("localStorage.getItem", "groupsplit.timeZone").SetResult("Europe/London");
        JSInterop.Setup<string[]>("gs.timeZones").SetResult(["Europe/London", "America/New_York", "Asia/Tokyo"]);
        JSInterop.Setup<string?>("gs.timeFormat").SetResult("system");
        var picker = Render<TimeZonePicker>();
        picker.Find("input[type=search]").Input("new york");
        var options = picker.Find("select").QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToArray();
        Assert.Contains("America/New_York", options);
        Assert.Contains("Europe/London", options);
        Assert.DoesNotContain("Asia/Tokyo", options);
        picker.Find("input[type=search]").Input("missing");
        Assert.Contains("No matching time zones", picker.Find("[role=status]").TextContent);
    }

    [Fact]
    public void Choosing_a_zone_updates_the_clock_and_saves_the_choice()
    {
        JSInterop.Setup<string?>("localStorage.getItem", "groupsplit.timeZone").SetResult(null);
        JSInterop.Setup<string[]>("gs.timeZones").SetResult(["America/New_York"]);
        JSInterop.Setup<string?>("gs.timeFormat").SetResult("system");
        JSInterop.Setup<string?>("gs.setTimeZone", "America/New_York").SetResult("America/New_York");
        var picker = Render<TimeZonePicker>();
        picker.WaitForAssertion(() => Assert.Equal(2, picker.FindAll("select")[0].QuerySelectorAll("option").Length));
        picker.Find("select").Change("America/New_York");
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
