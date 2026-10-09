namespace GroupSplit.Keycloak;

/// <summary>
/// The one Keycloak operation the API performs: removing somebody's login when they delete
/// their account. A port of its own, so the API depends on exactly what its
/// <c>manage-users</c> client is for and nothing the realm workers need.
/// </summary>
public interface IKeycloakUserRemoval
{
    /// <summary>
    /// Ends every session of the user with this id and deletes the user. Succeeds when there
    /// is no such user, so a retry after a success that was never reported is harmless.
    /// </summary>
    Task RemoveUserAsync(string id, CancellationToken ct = default);
}
