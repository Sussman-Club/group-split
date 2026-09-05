using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// Runs a page-state load and owns what happens when it fails. The state
/// services start their loads without awaiting them, so an exception there
/// would otherwise vanish and leave whatever was on screen before -- the
/// previous group's balances under the new group's name.
/// </summary>
public sealed class LoadGuard(IAuthService auth, NavigationManager nav, ISnackbar snackbar)
{
    /// <returns>Whether the load completed.</returns>
    public async Task<bool> RunAsync(Func<Task> load, string what)
    {
        try
        {
            await load();
            return true;
        }
        catch (ApiException exception) when (exception.StatusCode == 401)
        {
            // The BFF answers 401 once the session behind the cookie is gone
            // (it expired, or the server restarted and dropped its ticket
            // store). Nothing here can recover that; sign-in can.
            try
            {
                await auth.Login("/" + nav.ToBaseRelativePath(nav.Uri));
            }
            catch (Exception)
            {
                // A navigation exception is how a prerender redirects; nothing
                // to do with it here.
            }

            return false;
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or TaskCanceledException)
        {
            snackbar.Add($"Could not load {what}. Please try again.", Severity.Error);
            return false;
        }
    }
}
