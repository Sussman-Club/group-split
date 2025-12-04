using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Users;

public interface IUserLogin
{
    bool LoginAttempted { get; }
    UserInfo? User { get; }
    
    event AuthenticationStateChangedHandler? OnLoginChanged;
    
    Task RefreshLoginAsync();
    Task ClearLogin();
}

public delegate void AuthenticationStateChangedHandler(UserInfo? task);