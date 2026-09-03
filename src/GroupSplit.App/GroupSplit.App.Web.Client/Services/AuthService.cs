using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Web.Client.Services;

/// <summary>
/// A full page load is required: the OIDC challenge and cookie belong to the BFF.
/// </summary>
public class AuthService(NavigationManager nav) : IAuthService
{
    public Task Login(string? returnUrl, CancellationToken ct)
    {
        nav.NavigateTo(AuthRoutes.Login(returnUrl), forceLoad: true);
        return Task.CompletedTask;
    }

    public Task Logout(CancellationToken ct)
    {
        nav.NavigateTo(AuthRoutes.Logout, forceLoad: true);
        return Task.CompletedTask;
    }
}
