using GroupSplit.Data.Entities;

namespace GroupSplit.Data.Extensions;

/// <summary>
/// The group every account keeps its own spending in, and the one rule that says which
/// group that is: its id is the account's id.
/// </summary>
/// <remarks>
/// The id is the marker rather than a column, so there is nothing to keep in step: a
/// group can only be somebody's personal one by having been made with their id, and no
/// other group can collide with it. It also answers "is this the caller's personal
/// group" without a query, which matters because every guard asks exactly that.
/// <para>
/// It is a group of one. Its owner is its only member and always will be, so everything
/// that needs a second person -- members, guests, invitations, join links, split rules,
/// settlements, leaving, archiving -- is refused for it, and its balance is always zero.
/// </para>
/// </remarks>
public static class PersonalGroupExtensions
{
    /// <summary>What a personal group is called when it is made.</summary>
    public const string Name = "Personal";

    extension(Group group)
    {
        /// <summary>Whether this is <paramref name="userId"/>'s personal group.</summary>
        public bool IsPersonalOf(Guid userId) => group.Id == userId;
    }

    extension(Transaction transaction)
    {
        /// <summary>
        /// Whether this sits in its payer's personal group.
        /// </summary>
        /// <remarks>
        /// Answerable from the transaction alone because only the owner can pay in a
        /// personal group, so the group's id and the payer's are the same one. The
        /// navigation first, for the same reason as <see cref="TransactionExtensions"/>'s
        /// payer: a draft is not always keyed.
        /// </remarks>
        public bool IsPersonal => (transaction.Group?.Id ?? transaction.GroupId) == transaction.Payer;
    }

    extension(User user)
    {
        /// <summary>
        /// A new personal group for <paramref name="user"/>, with them as its only member.
        /// </summary>
        /// <remarks>
        /// Made here rather than through the service that creates groups, which gives every
        /// new group a member rule, a Bill rule and an Even default: a personal group has
        /// nobody to divide anything with, so it starts with no rules at all and an
        /// expense in it is the payer's alone.
        /// </remarks>
        public Group NewPersonalGroup()
        {
            var group = new Group { Id = user.Id, Name = Name };
            group.Users.Add(user);
            return group;
        }
    }
}
