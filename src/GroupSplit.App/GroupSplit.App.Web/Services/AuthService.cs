using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace GroupSplit.App.Web.Services;

public class AuthService(NavigationManager nav, IJSRuntime js, AntiforgeryStateProvider antiforgery, IPushNotifications? push = null) : IAuthService
{
    public Task Login(string? returnUrl, CancellationToken ct)
    {
        nav.NavigateTo(AuthRoutes.Login(returnUrl), forceLoad: true);
        return Task.CompletedTask;
    }

    public async Task Logout(CancellationToken ct)
    {
        if (push is not null)
            await push.UnregisterAsync();
        await SignOutForm.PostAsync(js, antiforgery, ct);
    }

    public async Task SignOutDeletedAccount(CancellationToken ct)
    {
        if (push is not null)
            await push.UnregisterAsync(accountDeleted: true);
        nav.NavigateTo(AuthRoutes.AccountDeleted, forceLoad: true);
    }
}
