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
    public async Task Categories_are_independent_and_failed_updates_show_a_warning()
    {
        var preferences = new Preferences();
        Services.AddSingleton<IPushNotifications>(preferences);
        var card = Render<PushNotificationsCard>();
        var switches = card.FindComponents<MudSwitch<bool>>();
        Assert.Equal(4, switches.Count);
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
        public bool Supported => true;
        public bool Enabled => enabled.Count > 0;
        public bool IsEnabled(PushNotificationKind kind) => enabled.Contains(kind);
        public Task SetEnabledAsync(bool value) => SetEnabledAsync(PushNotificationKind.ExpenseCreated, value);
        public Task SetEnabledAsync(PushNotificationKind kind, bool value)
        {
            if (Fail) throw new HttpRequestException("Offline");
            if (value) enabled.Add(kind); else enabled.Remove(kind);
            return Task.CompletedTask;
        }
        public Task RefreshAsync(Guid? userId = null) => Task.CompletedTask;
        public Task UnregisterAsync(bool accountDeleted = false) { enabled.Clear(); return Task.CompletedTask; }
    }
}
