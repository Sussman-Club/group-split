using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Web.Services;

public class AuthService(NavigationManager nav) : IAuthService
{
    public Task Login(CancellationToken ct)
    {
        nav.NavigateTo("/auth/login", forceLoad: true);
        return Task.CompletedTask;
    }

    public Task Register(CancellationToken ct)
    {
        nav.NavigateTo("/auth/register", forceLoad: true);
        return Task.CompletedTask;
    }

    public Task Logout(CancellationToken ct)
    {
        nav.NavigateTo("/auth/logout", forceLoad: true);
        return Task.CompletedTask;
    }
}