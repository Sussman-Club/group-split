using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;

namespace GroupSplit.App.Services;

public class AuthService(IIdentityClient identityClient) : IAuthService
{
    public AccessTokenResponse? AccessTokenResponse { get; set; }

    public async Task Login(LoginRequest request, CancellationToken ct)
    {
        //AccessTokenResponse = await identityClient.PostIdentityLoginAsync(false, false, request, ct);
        AccessTokenResponse = await identityClient.LoginAsync(request, false, false, ct);
    }

    public Task Logout()
    {
        throw new NotImplementedException();
    }

    public async Task Register(RegisterRequest request, CancellationToken ct)
    {
        //await identityClient.PostIdentityRegisterAsync(request, ct);
        await identityClient.RegisterAsync(request, ct);
    }
}
