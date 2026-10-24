using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Users;

public interface IUserLogin
{
    bool LoginAttempted { get; }
    bool LoginFailed { get; }
    UserInfo? User { get; }

    /// <summary>
    /// Set when the API refused the signed-in person outright, so the app shows why
    /// instead of rendering pages whose every request would be refused the same way.
    /// </summary>
    SignInRefusal? Refusal { get; }

    event AuthenticationStateChangedHandler? OnLoginChanged;

    Task RefreshLoginAsync();
    Task ClearLogin();
}

public delegate void AuthenticationStateChangedHandler(UserInfo? task);
