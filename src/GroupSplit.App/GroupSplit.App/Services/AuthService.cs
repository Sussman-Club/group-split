using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Users;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace GroupSplit.App.Services;

public sealed class AuthService(NativeSession session, IUserLogin user, NavigationManager navigation,
    ISnackbar snackbar, IPushNotifications? push = null) : IAuthService
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
        // Remove the server registration while the session is still authenticated.
        // If offline, deleting the Firebase token stops future delivery to this installation.
        try { if (push is not null) await push.UnregisterAsync(deleted); }
        catch (Exception) { snackbar.Add("Could not remove this device's alerts. Check notification settings before sharing this device.", Severity.Warning); }
        try { await session.LogoutAsync(deleted, ct); }
        catch (NativeCredentialCleanupException)
        {
            snackbar.Add("Signed out for now, but saved sign-in could not be cleared. Clear this app's storage before using it again.", Severity.Warning);
        }
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
