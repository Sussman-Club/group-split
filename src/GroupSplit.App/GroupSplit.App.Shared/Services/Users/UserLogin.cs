using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

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

    public SignInRefusal? Refusal
    {
        get => tracker.Refusal;
        private set => tracker.Refusal = value;
    }

    public bool LoginFailed
    {
        get => tracker.LoginFailed;
        private set => tracker.LoginFailed = value;
    }

    public event AuthenticationStateChangedHandler? OnLoginChanged;

    public Task ClearLogin()
    {
        User = null;
        Refusal = null;
        LoginFailed = false;
        OnLoginChanged?.Invoke(User);
        return Task.CompletedTask;
    }

    public async Task RefreshLoginAsync()
    {
        LoginFailed = false;
        try
        {
            User = await usersClient.GetCurrentUserAsync();
            Refusal = null;
        }
        catch (Exception exception)
        {
            User = null;
            Refusal = RefusalOf(exception);
            LoginFailed = Refusal is null &&
                (!ApiErrors.IsApiFailure(exception) ||
                 ApiErrors.Read(exception).Kind != ApiErrorKind.Unauthenticated);
        }

        LoginAttempted = true;
        OnLoginChanged?.Invoke(User);
    }

    /// <summary>
    /// The refusals that mean this person cannot use the app at all, as opposed to the
    /// ordinary reasons there is no user yet: not signed in, or a server that did not
    /// answer this once.
    /// </summary>
    private static SignInRefusal? RefusalOf(Exception exception)
    {
        if (!ApiErrors.IsApiFailure(exception))
        {
            return null;
        }

        var error = ApiErrors.Read(exception);

        return error.Code == ErrorCodes.IdentityEmailInUse
            ? new SignInRefusal(error.Code, error.TraceId)
            : null;
    }
}
