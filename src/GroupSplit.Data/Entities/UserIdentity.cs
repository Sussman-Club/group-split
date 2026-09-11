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
/// It is also what a deleted account gives up. The person's rows stay -- other members'
/// balances are made of them -- and the identity goes, so nobody can sign in as them again.
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
