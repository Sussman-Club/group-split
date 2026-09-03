using GroupSplit.App.Shared.Services;

namespace GroupSplit.App.Services;

public class AuthService : IAuthService
{
    public Task Register(string? returnUrl = null, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task Login(string? returnUrl = null, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task Logout(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
