using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using System.Net.Http.Json;

namespace GroupSplit.App.Web.Client.Services;

public class AuthService(HttpClient client, UserClientAuthenticationStateProvider authStateProvider) : IAuthService
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
        await authStateProvider.NotifyLoggedInAsync();
    }

    public async Task Logout()
    {
        var response = await client.PostAsync("auth/logout", null);
        response.EnsureSuccessStatusCode();
        await authStateProvider.NotifyLoggedOutAsync();
    }
}