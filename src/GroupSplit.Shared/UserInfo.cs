using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// One person a group can point at: a member, somebody it has invited and is still waiting
/// on, or somebody who took part in its history and has since left.
/// </summary>
/// <param name="IsPendingInvitee">
/// True when this person has been invited to the group being read and has not answered.
/// They can be given a share and can be the payer of an expense, and their shares count in
/// the group's balances -- but they have not joined, so nothing in the app should show them
/// as though they had, and there is nobody to settle up with yet.
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
    bool IsPendingInvitee = false,
    bool IsPastMember = false)
{
    /// <summary>
    /// What to call them. The address when there is no name, which is the ordinary case for
    /// somebody who was invited and has never signed in: there is no profile to read a name
    /// out of, and an empty label is worse than the address the group typed in itself.
    /// </summary>
    [JsonIgnore]
    public string FullName =>
        $"{FirstName} {LastName}".Trim() is { Length: > 0 } name ? name : Email ?? string.Empty;
}
