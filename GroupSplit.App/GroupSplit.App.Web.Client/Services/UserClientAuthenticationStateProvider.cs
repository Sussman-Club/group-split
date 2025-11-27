using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Client.Services;

public class UserClientAuthenticationStateProvider(IUsersClient usersClient) : AuthenticationStateProvider
{
    private AuthenticationState _cached = AnonymousState;
    private static readonly AuthenticationState AnonymousState =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private bool _initialized;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_initialized)
            return _cached;

        await RefreshAsync();
        _initialized = true;
        return _cached;
    }

    public async Task RefreshAsync()
    {
        try
        {
            var userInfo = await usersClient.GetCurrentUserAsync();
            // If call succeeds, create principal. (UserInfo only has Id.)
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, $"{userInfo.LastName}, {userInfo.FirstName}"),
                ],
                authenticationType: "Bearer");

            _cached = new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (ApiException ex) when (ex.StatusCode == 401 || ex.StatusCode == 403)
        {
            _cached = AnonymousState;
        }
        catch
        {
            _cached = AnonymousState;
        }

        NotifyAuthenticationStateChanged(Task.FromResult(_cached));
    }

    // Called after login to re-check server state
    public Task NotifyLoggedInAsync() => RefreshAsync();

    // Called after logout
    public Task NotifyLoggedOutAsync()
    {
        _cached = AnonymousState;
        NotifyAuthenticationStateChanged(Task.FromResult(_cached));
        return Task.CompletedTask;
    }
}