using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Users;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace GroupSplit.App.Services;

public sealed class AuthService(MobileSession session, IUserLogin user, NavigationManager navigation,
    ISnackbar snackbar) : IAuthService
{
    public async Task Login(string? returnUrl = null, bool remember = false, CancellationToken ct = default)
    {
        await session.LoginAsync(remember, ct);
        await user.RefreshLoginAsync();
        navigation.NavigateTo(LocalUrl.IsLocal(returnUrl) ? returnUrl : "/");
    }

    public Task Logout(CancellationToken ct = default) => SignOutAsync(false, ct);

    public Task SignOutDeletedAccount(CancellationToken ct = default) => SignOutAsync(true, ct);

    private async Task SignOutAsync(bool deleted, CancellationToken ct)
    {
        try { await session.LogoutAsync(deleted, ct); }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            snackbar.Add("Signed out of this app. Browser sign-out could not be completed; you may still be signed in there.", Severity.Warning);
        }
        finally
        {
            await user.ClearLogin();
            navigation.NavigateTo(deleted ? AuthRoutes.Handoff(HandoffReason.AccountDeleted) : "/login");
        }
    }
}
