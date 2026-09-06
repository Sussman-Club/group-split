using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Auth;

/// <summary>
/// Answers "what bearer token should this request carry", in the order that lets both a
/// person and a machine use the same binary:
/// <list type="number">
/// <item><c>GROUPSPLIT_TOKEN</c>, used verbatim -- the headless path, for CI and agents.</item>
/// <item>The stored credential, refreshed silently when it has expired.</item>
/// <item>Nothing, which is exit code 2 and an instruction to sign in.</item>
/// </list>
/// </summary>
public sealed class TokenProvider(
    TokenStore store,
    DeviceCodeFlow flow,
    OidcDiscovery discovery,
    Endpoints endpoints)
{
    private string? _cached;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariables.Token);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (_cached is not null)
        {
            return _cached;
        }

        var credential = store.Get(endpoints.Authority, endpoints.ClientId)
                         ?? throw CliException.Auth(
                             $"Not signed in to {endpoints.Authority}.",
                             $"Run: groupsplit auth login   (or set {EnvironmentVariables.Token} for non-interactive use)");

        if (!credential.IsExpired)
        {
            return _cached = credential.AccessToken;
        }

        if (string.IsNullOrEmpty(credential.RefreshToken))
        {
            throw CliException.Auth(
                "Your session has expired.",
                "Run: groupsplit auth login",
                ErrorCodes.AuthExpired);
        }

        var oidc = await discovery.GetAsync(endpoints.Authority, ct);
        var refreshed = await flow.RefreshAsync(oidc, endpoints.ClientId, credential.RefreshToken, ct);

        if (refreshed.Error is not null)
        {
            // The refresh token is spent or revoked. Clearing it means the next command
            // says "sign in" instead of failing the same way again.
            store.Remove(endpoints.Authority, endpoints.ClientId);

            throw CliException.Auth(
                "Your session has expired.",
                "Run: groupsplit auth login",
                ErrorCodes.AuthExpired);
        }

        store.Save(endpoints.Authority, endpoints.ClientId, Persist(refreshed));

        return _cached = refreshed.AccessToken;
    }

    /// <summary>Turns a token response into what goes on disk, including the claims shown by `auth status`.</summary>
    public static StoredCredential Persist(TokenResponse token)
    {
        var (username, subject, expiresAt) = JwtClaims.Read(token.AccessToken);

        return new StoredCredential
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            // Prefer the token's own exp over expires_in: it is what the API will enforce,
            // and it does not drift with however long the response took to arrive.
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            Username = username,
            Subject = subject
        };
    }
}
