using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Services;

// NOTE: The BFF invokes AuthService from the client-side only.
// This implementation exists only to satisfy DI requirements.
public class AuthService : IAuthService
{
    public Task Login(LoginRequest request, CancellationToken ct)
    {
        throw new InvalidOperationException("Auth operations are not supported on server-side.");
    }

    public Task Register(RegisterRequest request, CancellationToken ct)
    {
        throw new InvalidOperationException("Auth operations are not supported on server-side.");
    }

    public Task Logout()
    {
        throw new InvalidOperationException("Auth operations are not supported on server-side.");
    }
}