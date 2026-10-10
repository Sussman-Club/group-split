using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace GroupSplit.App.Web.Client.Services;

/// <summary>
/// A full page load is required: the OIDC challenge and cookie belong to the BFF.
/// </summary>
public class AuthService(NavigationManager nav, IJSRuntime js, AntiforgeryStateProvider antiforgery) : IAuthService
{
    public Task Login(string? returnUrl, bool remember, CancellationToken ct)
    {
        nav.NavigateTo(AuthRoutes.Login(returnUrl, remember), forceLoad: true);
        return Task.CompletedTask;
    }

    public Task Logout(CancellationToken ct) => SignOutForm.PostAsync(js, antiforgery, ct);

    public Task SignOutDeletedAccount(CancellationToken ct)
    {
        nav.NavigateTo(AuthRoutes.AccountDeleted, forceLoad: true);
        return Task.CompletedTask;
    }
}
