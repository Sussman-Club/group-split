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

    public Task<bool> InviteAsync(Guid groupId, string groupName, AddMemberRequest request,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.InviteToGroupAsync(groupId, request, ct);

            var count = request.UserIdentifiers.Count;

            snackbar.Add(
                count == 1
                    ? $"Invited {request.UserIdentifiers.First().Email} to {groupName}."
                    : $"Invited {count} people to {groupName}.",
                Severity.Success);

            // Nobody has joined yet, so the membership has not moved -- but the group's
            // pending list has, and that is read off the same announcement.
            await changes.NotifyGroupsChangedAsync();
        }, "Could not send the invitation.");

    public Task<bool> WithdrawInvitationAsync(Guid groupId, Guid invitationId, string email,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.WithdrawGroupInvitationAsync(groupId, invitationId, ct);
            snackbar.Add($"Invitation to {email} withdrawn.", Severity.Success);
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

    public async Task<GroupResponse?> AcceptInvitationAsync(Guid invitationId, string groupName,
        CancellationToken ct = default)
    {
        GroupResponse? joined = null;

        var done = await errors.TryAsync(async () =>
        {
            joined = await invitations.AcceptInvitationAsync(invitationId, ct);
            snackbar.Add($"You have joined {joined.Name}.", Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, $"Could not join {groupName}.");

        return done ? joined : null;
    }

    public Task<bool> DeclineInvitationAsync(Guid invitationId, string groupName,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await invitations.DeclineInvitationAsync(invitationId, ct);
            snackbar.Add($"Invitation to {groupName} declined.", Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, "Could not decline the invitation.");
}
