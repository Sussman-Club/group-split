using GroupSplit.App.Shared.Extensions;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="IGroupCommands"/>
/// <remarks>
/// The snackbar messages name the thing that changed. "Group created successfully" told
/// somebody who had just typed a name and pressed a button the one thing they already knew;
/// "Lisbon created" tells them which of the two attempts landed.
/// </remarks>
public sealed class GroupCommands(
    IGroupsClient groups,
    IInvitationsClient invitations,
    ApiErrorPresenter errors,
    ISnackbar snackbar,
    DataChangeNotifier changes) : IGroupCommands
{
    public async Task<GroupResponse?> CreateAsync(CreateGroupRequest request, CancellationToken ct = default)
    {
        GroupResponse? created = null;

        var done = await errors.TryAsync(async () =>
        {
            created = await groups.CreateGroupAsync(request, ct);
            snackbar.Add($"{created.Name} created.", Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, "Could not create the group.");

        return done ? created : null;
    }

    public Task<bool> RenameAsync(Guid groupId, JsonPatchDocument<CreateGroupRequest> patch,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            var group = await groups.UpdateGroupAsync(groupId, patch, ct);
            snackbar.Add($"Group renamed to {group.Name}.", Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, "Could not rename the group.");

    public Task<bool> InviteAsync(Guid groupId, string groupName, InviteToGroupRequest request,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.InviteToGroupAsync(groupId, request, ct);

            var count = request.Names.Count;

            // "Added" would be the wrong word and the old one: nothing has been sent
            // anywhere. The group has named somebody it can start splitting with, and the
            // link that reaches them is on the members page for whenever they want it.
            snackbar.Add(
                count == 1
                    ? $"{request.Names[0]} added to {groupName}. Send them their link when you like."
                    : $"{count} people added to {groupName}. Send each of them their link when you like.",
                Severity.Success);

            // Nobody has joined yet, so the membership has not moved -- but the group's
            // pending list has, and that is read off the same announcement.
            await changes.NotifyGroupsChangedAsync();
        }, "Could not send the invitation.");

    /// <summary>
    /// Takes an invitation back, saying what became of anything the group had already
    /// recorded against the person it named.
    /// </summary>
    /// <remarks>
    /// The second message is the whole reason this reads the answer at all. An invited
    /// person can be carrying shares -- that is what inviting somebody makes possible --
    /// and those go to a member rather than disappearing, which is a change to that
    /// member's balance and has to be said out loud rather than left to be noticed.
    /// </remarks>
    public Task<bool> WithdrawInvitationAsync(Guid groupId, Guid invitationId, string name,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            var closed = await groups.WithdrawGroupInvitationAsync(groupId, invitationId, ct);

            snackbar.Add($"Invitation to {name} withdrawn.", Severity.Success);

            if (closed.MovedAnything && closed.AbsorbedByUserName is { } absorber)
            {
                snackbar.Add(
                    $"What was recorded against {name} is now {absorber}'s: " +
                    $"{closed.SharesMoved} share(s) and {closed.PaymentsMoved} payment(s). " +
                    "No amounts changed.",
                    Severity.Info);
            }

            await changes.NotifyGroupsChangedAsync();
        }, "Could not withdraw the invitation.");

    public async Task<GroupJoinLinkResponse?> CreateJoinLinkAsync(Guid groupId, string groupName,
        CancellationToken ct = default)
    {
        GroupJoinLinkResponse? link = null;

        var done = await errors.TryAsync(async () =>
        {
            link = await groups.CreateGroupJoinLinkAsync(groupId, ct);

            // The old link, if there was one, stopped working the moment this one was made,
            // so the message says the part somebody could otherwise be caught out by.
            snackbar.Add($"New join link for {groupName}. Any link you shared before has stopped working.",
                Severity.Success);

            await changes.NotifyGroupsChangedAsync();
        }, "Could not make a join link.");

        return done ? link : null;
    }

    public Task<bool> RevokeJoinLinkAsync(Guid groupId, string groupName, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.RevokeGroupJoinLinksAsync(groupId, ct);
            snackbar.Add($"The join link to {groupName} no longer works.", Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, "Could not withdraw the join link.");

    public async Task<JoinedGroupResponse?> JoinByLinkAsync(string token, CancellationToken ct = default)
    {
        JoinedGroupResponse? joined = null;

        var done = await errors.TryAsync(async () =>
        {
            joined = await invitations.AcceptJoinLinkAsync(token, ct);

            // Being in it already is not a failure and does not read as one: the person
            // followed a link to a group they are in, and the app takes them there.
            snackbar.Add(
                joined.AlreadyAMember
                    ? $"You are already in {joined.GroupName}."
                    : $"You have joined {joined.GroupName}.",
                joined.AlreadyAMember ? Severity.Info : Severity.Success);

            await changes.NotifyGroupsChangedAsync();
        }, "Could not join the group.");

        return done ? joined : null;
    }

    public Task<bool> RemoveMemberAsync(Guid groupId, Guid memberUserId, string memberName,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.RemoveGroupMemberAsync(groupId, memberUserId, ct);
            snackbar.Add($"{memberName} removed from the group.", Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, "Could not remove the member.");

    public Task<bool> LeaveAsync(Guid groupId, string groupName, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.LeaveGroupAsync(groupId, ct);
            snackbar.Add($"You have left {groupName}.", Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, "Could not leave the group.");

    public Task<bool> SetArchivedAsync(Guid groupId, string groupName, bool archived,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            if (archived)
                await groups.ArchiveGroupAsync(groupId, ct);
            else
                await groups.UnarchiveGroupAsync(groupId, ct);

            snackbar.Add(
                archived
                    ? $"{groupName} archived. Only you stop seeing it in your list."
                    : $"{groupName} is back in your list.",
                Severity.Success);

            await changes.NotifyGroupsChangedAsync();
        }, archived ? "Could not archive the group." : "Could not unarchive the group.");

    public Task<bool> SettleAsync(Guid groupId, SettleRequest request, string otherName,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.SettleGroupDebtsAsync(groupId, request, ct);

            snackbar.Add(
                request.Direction is SettlementDirection.YouPaidThem
                    ? $"Recorded: you paid {otherName} {request.Amount.ToMoney()}."
                    : $"Recorded: {otherName} paid you {request.Amount.ToMoney()}.",
                Severity.Success);

            // A transfer moves both balances, and it is a transaction, so it is the
            // transaction announcement the group page listens to for its figures.
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not record the settlement.");

    public async Task<SettleUpResponse?> SettleUpAsync(Guid groupId, SettleUpRequest request,
        CancellationToken ct = default)
    {
        SettleUpResponse? settled = null;

        var done = await errors.TryAsync(async () =>
        {
            settled = await groups.SettleUpAsync(groupId, request, ct);

            snackbar.Add(
                $"Settled up: {settled.Payments.Count} "
                + $"{(settled.Payments.Count is 1 ? "repayment" : "repayments")}, "
                + $"{settled.Total.ToMoney()} in all.",
                Severity.Success);

            // Transfers are transactions, so this is the announcement the group page's
            // figures listen to -- the same one recording a single repayment makes.
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not settle up.");

        return done ? settled : null;
    }

    /// <summary>
    /// Claims a personal invitation: joins the group, as the person the group named.
    /// </summary>
    /// <remarks>
    /// The second message is the whole reason this reads the answer. Claiming is not only
    /// joining: whatever the group had recorded against that name -- shares, and anything
    /// they were down as having paid for -- is the claimer's from this moment. No amount
    /// changes, and their balance in the group does, so it is said rather than left to be
    /// noticed on the next screen.
    /// </remarks>
    public async Task<InvitationClaimedResponse?> ClaimInvitationAsync(string token,
        CancellationToken ct = default)
    {
        InvitationClaimedResponse? claimed = null;

        var done = await errors.TryAsync(async () =>
        {
            claimed = await invitations.ClaimInvitationAsync(token, ct);

            snackbar.Add($"You have joined {claimed.GroupName} as {claimed.Name}.", Severity.Success);

            if (claimed.TookAnything)
            {
                snackbar.Add(
                    $"{claimed.SharesTaken} share(s) recorded against {claimed.Name} are yours now. " +
                    "No amounts changed.",
                    Severity.Info);
            }

            await changes.NotifyGroupsChangedAsync();
        }, "Could not claim the invitation.");

        return done ? claimed : null;
    }

    /// <summary>
    /// Turns an invitation down, saying so when the group had already been recording money
    /// against the person it named.
    /// </summary>
    /// <remarks>
    /// Whoever declines is entitled to know, even though it is not theirs to sort out: a
    /// group that has been splitting the rent against that name for a fortnight has a
    /// position in it, and "declined" alone would leave them wondering what happened to it.
    /// It went to a member of the group, unchanged.
    /// </remarks>
    public Task<bool> DeclineInvitationAsync(string token, string groupName,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            var closed = await invitations.DeclineInvitationAsync(token, ct);

            snackbar.Add($"Invitation to {groupName} declined.", Severity.Success);

            if (closed.MovedAnything && closed.AbsorbedByUserName is { } absorber)
            {
                snackbar.Add(
                    $"{groupName} had recorded {closed.SharesMoved} share(s) against " +
                    $"{closed.Name}. That is now {absorber}'s, with no amounts changed.",
                    Severity.Info);
            }

            await changes.NotifyGroupsChangedAsync();
        }, "Could not decline the invitation.");
}
