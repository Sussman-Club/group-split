using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public sealed class PushNotificationsCardTest : ComponentTest
{
    [Fact]
    public async Task Updating_a_preference_disables_all_switches_until_it_finishes()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preferences = new Preferences { Pending = completion.Task };
        Services.AddSingleton<IPushNotifications>(preferences);
        var card = Render<PushNotificationsCard>();
        var control = card.FindComponents<MudSwitch<bool>>().First();

        var update = card.InvokeAsync(() => control.Instance.ValueChanged.InvokeAsync(true));
        card.WaitForAssertion(() =>
        {
            Assert.Equal("true", card.Find(".gs-notifications").GetAttribute("aria-busy"));
            Assert.All(card.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));
        });

        completion.SetResult();
        await update;
        card.WaitForAssertion(() => Assert.All(card.FindAll("input"), input => Assert.False(input.HasAttribute("disabled"))));
        Assert.True(preferences.IsEnabled(PushNotificationKind.ExpenseCreated));
    }

    [Fact]
    public async Task Categories_are_independent_and_failed_updates_show_a_warning()
    {
        var preferences = new Preferences();
        Services.AddSingleton<IPushNotifications>(preferences);
        var card = Render<PushNotificationsCard>();
        var switches = card.FindComponents<MudSwitch<bool>>();
        Assert.Equal(4, switches.Count);
        foreach (var input in card.FindAll("input"))
        {
            var label = card.Find($"#{input.GetAttribute("aria-labelledby")}");
            Assert.Contains(label.TextContent, PushNotificationKinds.Settings.Select(PushNotificationKinds.Label));
        }
        Assert.Contains("Expenses", card.Markup);
        Assert.DoesNotContain("Expense changes", card.Markup);
        Assert.Equal(PushNotificationKinds.Preference(PushNotificationKind.ExpenseCreated),
            PushNotificationKinds.Preference(PushNotificationKind.ExpenseChanged));
        var settlements = switches.Single(s => s.Markup.Contains("Settlements"));
        await card.InvokeAsync(() => settlements.Instance.ValueChanged.InvokeAsync(true));
        Assert.True(preferences.IsEnabled(PushNotificationKind.Settlement));
        Assert.False(preferences.IsEnabled(PushNotificationKind.ExpenseCreated));
        preferences.Fail = true;
        Assert.DoesNotContain("Payment requests", card.Markup);
        Assert.Equal(PushNotificationKinds.Preference(PushNotificationKind.Settlement),
            PushNotificationKinds.Preference(PushNotificationKind.PaymentRequest));
        var requests = card.FindComponents<MudSwitch<bool>>().Single(s => s.Markup.Contains("Guests joining"));
        await card.InvokeAsync(() => requests.Instance.ValueChanged.InvokeAsync(true));
        Assert.Contains("Could not update alerts", card.Markup);
        Assert.False(preferences.IsEnabled(PushNotificationKind.InvitationAccepted));
        Assert.True(preferences.IsEnabled(PushNotificationKind.Settlement));
    }

    private sealed class Preferences : IPushNotifications
    {
        private readonly HashSet<PushNotificationKind> enabled = [];
        public bool Fail { get; set; }
        public Task? Pending { get; set; }
        public bool Supported => true;
        public bool Enabled => enabled.Count > 0;
        public bool IsEnabled(PushNotificationKind kind) => enabled.Contains(kind);
        public Task SetEnabledAsync(bool value) => SetEnabledAsync(PushNotificationKind.ExpenseCreated, value);
        public async Task SetEnabledAsync(PushNotificationKind kind, bool value)
        {
            if (Pending is not null) await Pending;
            if (Fail) throw new HttpRequestException("Offline");
            if (value) enabled.Add(kind); else enabled.Remove(kind);
        }
        public Task RefreshAsync(Guid? userId = null) => Task.CompletedTask;
        public Task UnregisterAsync(bool accountDeleted = false) { enabled.Clear(); return Task.CompletedTask; }
    }
}
