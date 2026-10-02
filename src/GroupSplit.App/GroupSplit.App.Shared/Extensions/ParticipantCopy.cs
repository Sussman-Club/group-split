namespace GroupSplit.App.Shared.Extensions;

/// <summary>
/// The words the app uses for what somebody is to a group: invited, left, or about to be
/// merged into somebody already in it.
/// </summary>
/// <remarks>
/// One place, the way <c>ErrorMessages</c> is one place for refusals, because each of these
/// restates a row of <c>docs/participants.md</c> and has to say the same thing on every
/// screen that shows it. Not localisation: just one copy of each sentence.
/// </remarks>
public static class ParticipantCopy
{
    /// <summary>After the name of somebody invited who has not joined.</summary>
    public const string InvitedMark = "(invited)";

    /// <summary>After the name of somebody who was in the group and is not now.</summary>
    public const string LeftMark = "(left the group)";

    /// <summary>A share that cannot change, because its holder has left.</summary>
    public const string LeaverShareFixed = "Fixed: they settled up on this share before leaving.";

    public static string Invited(string name) => $"{name} {InvitedMark}";

    public static string Left(string name) => $"{name} {LeftMark}";

    /// <summary>
    /// What claiming an invitation does to somebody who is already a member: the one claim
    /// that changes an existing balance.
    /// </summary>
    public static string MergeWarning(string name) =>
        $"Claiming this merges {name} into your account. Their shares, anything they are " +
        $"down as having paid for, and their places in split rules become yours, and {name} " +
        "leaves the members list. No amounts change, but your balance does. Only do this if the group " +
        "named you twice.";
}
