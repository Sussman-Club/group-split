using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

public interface IAuthService
{
    Task Register(RegisterRequest request, CancellationToken ct);
    Task Login(LoginRequest request, CancellationToken ct);
    Task Logout();
}
