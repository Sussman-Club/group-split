namespace GroupSplit.App.Shared.Services;

public interface IAuthService
{
    Task Register(CancellationToken ct = default);
    Task Login(CancellationToken ct = default);
    Task Logout(CancellationToken ct = default);
}
