using System.Net.Http.Json;
using System.Text.Json;

namespace GroupSplit.AppHost.Test.Base;

/// <summary>
/// Just enough of Keycloak's admin API for the browser tests to set the realm up the way
/// they need it: a person to sign in as, and an access token short enough that a test can
/// outlive one.
/// </summary>
public sealed class KeycloakAdmin(HttpClient client, string username, string password)
{
    /// <summary>
    /// What the AppHost is given when nothing else has said, rather than letting Aspire
    /// generate a password the tests cannot read. Not a secret: on a fresh Keycloak it
    /// reaches a container that exists for the length of one test run.
    /// </summary>
    /// <remarks>
    /// A fallback and not an override, which is the whole of the distinction that matters
    /// here. See <see cref="AppHostFixture"/>.
    /// </remarks>
    public const string DefaultUsername = "admin";

    public const string DefaultPassword = "admin-for-tests";

    private const string Realm = "group-split";

    /// <summary>
    /// The password every account these tests create signs in with. Meets the realm's
    /// policy, which rejects anything shorter or simpler.
    /// </summary>
    public const string AccountPassword = "GroupSplit123!";

    /// <summary>
    /// Shortens the access token so that waiting out an expiry is a test rather than a
    /// coffee break. Sixty seconds is Keycloak's floor for this in practice, and it is
    /// under the one-minute refresh skew, so every call refreshes -- which is the point.
    /// </summary>
    internal const int AccessTokenLifespanSeconds = 60;

    public async Task ShortenAccessTokenLifespanAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"admin/realms/{Realm}")
        {
            Content = JsonContent.Create(new
            {
                realm = Realm,
                accessTokenLifespan = AccessTokenLifespanSeconds
            })
        };

        await SendAsync(request, ct);
    }

    /// <summary>
    /// Creates an account and returns the address it signs in with. Unique per call, so a
    /// test starts from an empty account and one test cannot see another's data.
    /// </summary>
    public async Task<string> CreateAccountAsync(CancellationToken ct)
    {
        var email = $"test-{Guid.NewGuid():N}@test.com";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"admin/realms/{Realm}/users")
        {
            Content = JsonContent.Create(new
            {
                username = email,
                email,
                emailVerified = true,
                enabled = true,
                firstName = "Test",
                lastName = "Person",
                credentials = new[]
                {
                    new { type = "password", value = AccountPassword, temporary = false }
                }
            })
        };

        await SendAsync(request, ct);

        return email;
    }

    /// <summary>
    /// Attaches a fresh admin token and reports the body on failure, because Keycloak
    /// explains a rejected realm or user in it and says nothing useful in the status.
    /// </summary>
    private async Task SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await TokenAsync(ct));

        using var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Keycloak answered {(int)response.StatusCode} to "
                + $"{request.Method} {request.RequestUri}: {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        using var response = await client.PostAsync(
            "realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = username,
                ["password"] = password
            }),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "Could not authenticate against Keycloak as the admin the AppHost was given: "
                + $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}."
                + Environment.NewLine
                + $"Signed in as '{username}'. Keycloak creates its admin once, when its database is "
                + "empty, and this stack's database outlives `aspire stop` -- so on a machine that has "
                + "run the AppHost normally the admin already exists with whatever password was "
                + "configured then. If that password has since changed, drop the `keycloak` database "
                + "on the `db-server` container and start again; the realm is re-imported with it.");
        }

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return payload.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("The admin token response carried no access token.");
    }
}
