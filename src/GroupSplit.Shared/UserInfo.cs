using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// One person a group can point at: a member, a guest, or somebody who took part in its
/// history and has since left.
/// </summary>
/// <param name="IsGuest">
/// True when this person is one of the group's guests: somebody it shares costs with who has
/// no account in it. They can be given a share, be the payer of an expense, be settled with
/// and be named in a repayment, and their shares count in the group's balances -- but the
/// members record all of it, so nothing in the app should show them as though they had
/// joined.
/// </param>
/// <param name="IsPastMember">
/// True when this person is no longer a member but is still present in the group's recorded
/// history. Past members are for context only: they must not appear in selectors for new
/// expenses, rules, or settlements.
/// </param>
public record UserInfo(
    Guid Id,
    string? FirstName,
    string? LastName,
    string? Email,
    bool IsGuest = false,
    bool IsPastMember = false,
    string? PreferredLanguage = null)
{
    /// <summary>The name <see cref="IsGuest"/> had while guests were called pending invitees.</summary>
    /// <remarks>
    /// Still written, so a client built before the rename -- the published CLI, an agent
    /// following the old skill -- reads the same answer. To be removed one release after the
    /// rename.
    /// </remarks>
    [Obsolete("Use IsGuest.")]
    public bool IsPendingInvitee
    {
        get => IsGuest;
        init => IsGuest = value;
    }

    /// <summary>
    /// What to call them. The address when there is no name, which is the ordinary case for
    /// a guest: there is no profile to read a name
    /// out of, and an empty label is worse than the address the group typed in itself.
    /// </summary>
    [JsonIgnore]
    public string FullName =>
        $"{FirstName} {LastName}".Trim() is { Length: > 0 } name ? name : Email ?? string.Empty;
}
