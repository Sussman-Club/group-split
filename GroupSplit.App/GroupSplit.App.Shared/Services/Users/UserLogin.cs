using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Users;

public class UserLogin(UserTracker tracker, IUsersClient usersClient) : IUserLogin
{
    public UserInfo? User
    {
        get => tracker.User;
        private set => tracker.User = value;
    }

    public event AuthenticationStateChangedHandler? OnLoginChanged;

    public Task ClearLogin()
    {
        User = null;
        OnLoginChanged?.Invoke(Task.FromResult(User));
        return Task.CompletedTask;
    }

    public async Task RefreshLoginAsync()
    {
        var task = GetUserInfoAsync();
        OnLoginChanged?.Invoke(task);
        await task;
        return;

        async Task<UserInfo?> GetUserInfoAsync()
        {
            try
            {
                User = await usersClient.GetCurrentUserAsync();
            }
            catch
            {
                User = null;
            }
            
            return User;
        }
    }
}



