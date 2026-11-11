namespace GroupSplit.Data.Entities;

/// <summary>
/// A deleted account whose login the identity provider still has.
/// </summary>
/// <remarks>
/// Deleting an account is two writes in two systems: anonymising the row here, and deleting
/// the login in Keycloak. They cannot share a transaction, so this row is written in the same
/// save as the anonymisation and removed once Keycloak confirms the login is gone. If Keycloak
/// cannot be reached the account is still deleted here, the row stays, and a sweep keeps
/// trying -- a deletion is never left half done with nothing to show for it.
/// <para>
/// In the meantime the login still works at Keycloak, and the app refuses it: the deleted
/// row keeps its <see cref="UserIdentity"/> as a tombstone, so a sign-in with that subject
/// finds an account marked <see cref="UserKind.Deleted"/> rather than nothing, and is told so
/// rather than handed a fresh, empty account.
/// </para>
/// </remarks>
public class PendingLoginDeletion : Entity
{
    /// <summary>The identity provider's subject, as <see cref="UserIdentity.IdentityId"/> holds it.</summary>
    public required string IdentityId { get; set; }

    public required DateTimeOffset RequestedAt { get; set; }

    /// <summary>How many times deleting the login has failed. Read by the sweep's log, never to give up.</summary>
    public int Attempts { get; set; }
}
