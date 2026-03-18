using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Users;

namespace GroupSplit.App.Web.Client.Services;

public class AuthService(HttpClient client, IUserLogin userLogin) : IAuthService
{
    public async Task Register(CancellationToken ct)
    {
        throw new NotImplementedException();
    }
    
    public async Task Login(CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public async Task Logout(CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}