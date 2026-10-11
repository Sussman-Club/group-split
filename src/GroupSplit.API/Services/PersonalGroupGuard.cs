using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services;

/// <summary>
/// The one refusal every path that needs a second person makes of a personal group.
/// </summary>
/// <remarks>
/// One method rather than a check per service, so the list of what a personal group cannot
/// do is the list of places that call this, and every one of them answers with the same
/// code. Asked of the caller because every such path is reached through a group the caller
/// is a member of, and the only member of a personal group is its owner.
/// </remarks>
internal static class PersonalGroupGuard
{
    /// <summary>
    /// 409 <c>PERSONAL_GROUP_NOT_SHARED</c> when <paramref name="groupId"/> is
    /// <paramref name="callerId"/>'s personal group.
    /// </summary>
    public static void RefuseIfPersonal(Guid groupId, Guid callerId)
    {
        if (groupId == callerId)
            throw new ConflictException(ErrorCodes.PersonalGroupNotShared,
                "This is your personal group. It is yours alone, so it has no members, guests, "
                + "split rules or settlements, and it cannot be left or archived.");
    }

    extension(Group group)
    {
        /// <inheritdoc cref="RefuseIfPersonal(Guid, Guid)"/>
        public void RefuseIfPersonalOf(Guid callerId) => RefuseIfPersonal(group.Id, callerId);
    }
}
