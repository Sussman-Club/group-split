using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Users;

public class UserLogin(UserTracker tracker, IUsersClient usersClient) : IUserLogin
{
    public UserInfo? User
    {
        get => tracker.User;
        private set => tracker.User = value;
    }

    public bool LoginAttempted
    {
        get => tracker.LoginAttempted;
        private set => tracker.LoginAttempted = value;
    }

    public event AuthenticationStateChangedHandler? OnLoginChanged;

    public Task ClearLogin()
    {
        User = null;
        OnLoginChanged?.Invoke(User);
        return Task.CompletedTask;
    }

    public async Task RefreshLoginAsync()
    {
        try
        {
            User = await usersClient.GetCurrentUserAsync();
        }
        catch
        {
            User = null;
        }

        LoginAttempted = true;
        OnLoginChanged?.Invoke(User);
    }
}