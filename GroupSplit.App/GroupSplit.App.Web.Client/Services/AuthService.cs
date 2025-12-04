using System.Net.Http.Json;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Client.Services;

public class AuthService(HttpClient client, IUserLogin userLogin) : IAuthService
{
    public async Task Register(RegisterRequest request, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("auth/register", request, ct);
        response.EnsureSuccessStatusCode();
    }
    
    public async Task Login(LoginRequest request, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("auth/login", request, ct);
        response.EnsureSuccessStatusCode();
        await userLogin.RefreshLoginAsync();
    }

    public async Task Logout()
    {
        var response = await client.PostAsync("auth/logout", null);
        response.EnsureSuccessStatusCode();
        await userLogin.ClearLogin();
    }
}