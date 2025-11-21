using GroupSplit.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.App.Shared.Services;

public interface IAuthService
{
    Task Login(LoginRequest request, CancellationToken ct);
    Task Register(RegisterRequest request, CancellationToken ct);
    Task Logout();
}
