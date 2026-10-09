using GroupSplit.Keycloak;

namespace GroupSplit.API.Test.Base;

/// <summary>
/// Keycloak's half of deleting an account, for hosts that have no Keycloak. Records which
/// logins were removed, and can be told to fail the way an unreachable Keycloak does.
/// </summary>
/// <remarks>
/// Registered by type in the test hosts, so every test's provider gets its own: one test's
/// outage must not be another test's.
/// </remarks>
public sealed class FakeKeycloakUserRemoval : IKeycloakUserRemoval
{
    private readonly List<string> _removed = [];

    /// <summary>The subjects removed, in order.</summary>
    public IReadOnlyList<string> Removed
    {
        get
        {
            lock (_removed) return [.. _removed];
        }
    }

    /// <summary>How many calls fail before one succeeds. <see cref="int.MaxValue"/> is an outage.</summary>
    public int FailuresBeforeSuccess { get; set; }

    public int Calls { get; private set; }

    public Task RemoveUserAsync(string id, CancellationToken ct = default)
    {
        lock (_removed)
        {
            Calls++;

            if (FailuresBeforeSuccess > 0)
            {
                FailuresBeforeSuccess--;
                throw new HttpRequestException("Connection refused (keycloak:8080)");
            }

            _removed.Add(id);
        }

        return Task.CompletedTask;
    }
}
