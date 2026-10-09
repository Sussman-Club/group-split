namespace GroupSplit.Data.Entities;

/// <summary>
/// The sign-in a <see cref="User"/> answers to, kept apart from the person it belongs to.
/// </summary>
/// <remarks>
/// A row of its own rather than a column on the user, because the two have different
/// lifetimes: a user exists from the moment a group names them, and an identity only from
/// the moment somebody actually signs in as them. Claiming an invitation adds one of these
/// to a user row that already holds shares, so the identity is the thing that arrives late.
/// <para>
/// A deleted account keeps it, as a tombstone. The person's rows stay -- other members'
/// balances are made of them -- and so does the subject, which is not personal data once the
/// login it named is gone from Keycloak: it is what lets a sign-in with that subject, made
/// before Keycloak has caught up, be refused as a deleted account rather than welcomed as a
/// new one. See <see cref="PendingLoginDeletion"/>.
/// </para>
/// </remarks>
public class UserIdentity : Entity
{
    public virtual User User { get; set; } = null!;

    /// <summary>
    /// The identity provider's subject claim -- Keycloak's <c>sub</c>. Opaque here, and the
    /// only thing that ties a token to a person; <c>UserProvisioner</c> is the one place
    /// that reads it.
    /// </summary>
    public string IdentityId { get; set; } = null!;
}
