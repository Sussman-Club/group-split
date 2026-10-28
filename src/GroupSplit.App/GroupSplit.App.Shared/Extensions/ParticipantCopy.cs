using GroupSplit.App.Shared.Localization;
namespace GroupSplit.App.Shared.Extensions;

/// <summary>
/// The words the app uses for what somebody is to a group: a guest, somebody who left, a
/// guest who was removed, or somebody about to be merged into a person already in it.
/// </summary>
/// <remarks>
/// One place, the way <c>ErrorMessages</c> is one place for refusals, because each of these
/// restates a row of <c>docs/participants.md</c> and has to say the same thing on every
/// screen that shows it. Not localisation: just one copy of each sentence.
/// </remarks>
public static class ParticipantCopy
{
    /// <summary>
    /// After the name of one of the group's guests: somebody it shares costs with who has no
    /// account in it.
    /// </summary>
    /// <remarks>
    /// It said "(invited)" while guests were pending invitees, and that word promised an
    /// answer most of them were never going to give. A guest is a settled state rather than
    /// a waiting one -- the members record everything for them -- so the mark names what
    /// they are, not what the group is hoping for.
    /// </remarks>
    public static string GuestMark => UiText.Get("(guest)");

    /// <summary>After the name of somebody who was in the group and is not now.</summary>
    public static string LeftMark => UiText.Get("(left the group)");

    /// <summary>
    /// After the name of a guest who was removed. Not <see cref="LeftMark"/>: a guest never
    /// joined, so they cannot have left, and an old expense naming them should not read as
    /// though a member had walked out.
    /// </summary>
    public static string RemovedGuestMark => UiText.Get("(removed guest)");

    /// <summary>
    /// The note under the guests on the People tab, said once for all of them rather than on
    /// every row.
    /// </summary>
    public static string GuestsNote => UiText.Get("Guests can't sign in. Members record everything for them.");

    /// <summary>A share that cannot change, because its holder has left.</summary>
    public static string LeaverShareFixed => UiText.Get("Fixed: they settled up on this share before leaving.");

    public static string Guest(string name) => $"{name} {GuestMark}";

    public static string Left(string name) => $"{name} {LeftMark}";

    public static string RemovedGuest(string name) => $"{name} {RemovedGuestMark}";

    /// <summary>
    /// Where a guest on the cross-group settling screens belongs. A guest is one group's,
    /// always, so the group is the one thing to say about them -- and saying "guest" there is
    /// what tells the reader that recording the repayment is theirs to do, since the guest
    /// cannot.
    /// </summary>
    public static string GuestIn(string groupName) => UiText.Format($"guest in {groupName}");

    /// <summary>
    /// What a guest's link does, said the moment it is copied. The link is a bearer
    /// credential on the guest's whole position, and whoever it reaches can take it -- so the
    /// message says that rather than only that the clipboard has it.
    /// </summary>
    public static string GuestLinkCopied(string name) =>
        UiText.Format($"Link for {name} copied. Whoever opens it can join as {name}.");

    /// <summary>
    /// The question before removing a guest. Says both halves of the rule: the balance has to
    /// be zero first, and nothing recorded for them goes away afterwards.
    /// </summary>
    public static string RemoveGuestQuestion(string name) =>
        UiText.Format($"Remove {name}? They have to be square with the group first. Their history stays.");

    /// <summary>
    /// What claiming a guest's link does to somebody who is already a member: the one claim
    /// that changes an existing balance.
    /// </summary>
    /// <remarks>
    /// "Leaves the guests", where it said the members list: a guest was never on that list,
    /// and the sentence is about the row that disappears from the People tab.
    /// </remarks>
    public static string MergeWarning(string name) =>
        UiText.Format($"Claiming this merges {name} into your account. Their shares, anything they are down as having paid for, and their places in split rules become yours, and {name} leaves the group's guests. No amounts change, but your balance does. Only do this if the group named you twice.");

    /// <summary>
    /// Why an expense or settlement cannot be deleted: somebody on it has left, and settled
    /// up on it before they went. Says what is still open, so the next try is not the same
    /// thing by another route.
    /// </summary>
    public static string CannotDelete(string leaver, string noun) =>
        UiText.Format($"{leaver} has left the group and settled up on this {UiText.Get(noun)} before leaving, so it stays as it was recorded. You can still edit it, as long as their part stays the same.");

    /// <summary>
    /// The question that confirms a merge. Short, because <see cref="MergeWarning"/> is on
    /// the page behind it: the dialog asks, it does not explain the same thing twice.
    /// </summary>
    public static string MergeQuestion(string name, string groupName) =>
        UiText.Format($"Are you and {name} the same person? Your balance in {groupName} will include theirs.");
}
