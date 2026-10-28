using GroupSplit.App.Shared.Localization;
using GroupSplit.App.Shared.Extensions;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
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
            snackbar.Add(UiText.Format($"{created.Name} created."), Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not create the group."));

        return done ? created : null;
    }

    /// <summary>
    /// Writes the group's settings. Said back as a rename when the name is in the patch,
    /// because that is what somebody notices; a patch that only moves the default split
    /// used to be announced as "renamed to" the name it already had.
    /// </summary>
    public Task<bool> RenameAsync(Guid groupId, JsonPatchDocument<CreateGroupRequest> patch,
        CancellationToken ct = default)
    {
        var renames = patch.Operations.Any(operation =>
            operation.path.TrimStart('/').Equals(nameof(CreateGroupRequest.Name), StringComparison.OrdinalIgnoreCase));

        return errors.TryAsync(async () =>
        {
            var group = await groups.UpdateGroupAsync(groupId, patch, ct);
            snackbar.Add(renames ? UiText.Format($"Group renamed to {group.Name}.") : UiText.Format($"{group.Name} updated."), Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not update the group."));
    }

    public Task<bool> AddGuestsAsync(Guid groupId, string groupName, AddGuestsRequest request,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            var added = await groups.AddGroupGuestsAsync(groupId, request, ct);

            // What the API added, rather than what was typed: it is the answer to what
            // happened, and the two only differ if the server had a reason to.
            var names = added.Select(guest => guest.Name).ToList();

            // "Added", and nothing about a link: nobody has been sent anything, and that is
            // the point. The group can split with them from this moment; a link is a
            // separate act on the People tab, for the few guests who will ever want one.
            snackbar.Add(
                names.Count == 1
                    ? UiText.Format($"{names[0]} added to {groupName} as a guest.")
                    : UiText.Format($"{names.Count} guests added to {groupName}."),
                Severity.Success);

            // The membership has not moved, but every screen that chooses a person has a
            // new one to offer -- and those read off the same announcement.
            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not add them."));

    public async Task<GroupGuestResponse?> IssueGuestLinkAsync(Guid groupId, Guid guestUserId, bool replace,
        CancellationToken ct = default)
    {
        GroupGuestResponse? guest = null;

        var done = await errors.TryAsync(async () =>
        {
            guest = await groups.IssueGuestLinkAsync(groupId, guestUserId,
                new IssueGuestLinkRequest { Replace = replace }, ct);

            // Announced even when the link was already out and nothing changed: the People
            // tab reads "link sent" off the guests listing, and a first link has to land
            // there. A re-read that finds nothing new costs one request.
            await changes.NotifyGroupsChangedAsync();
        }, replace ? UiText.Get("Could not make a new link.") : UiText.Get("Could not make a link."));

        return done ? guest : null;
    }

    public Task<bool> RevokeGuestLinkAsync(Guid groupId, Guid guestUserId, string name,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.RevokeGuestLinkAsync(groupId, guestUserId, ct);

            // Both halves, because "stop link" next to "remove" could be read as the gentler
            // of two ways to take somebody out. It is not that: they are still a guest.
            snackbar.Add(UiText.Format($"{name}'s link no longer works. {name} is still a guest."), Severity.Success);

            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not stop the link."));

    public async Task<GroupJoinLinkResponse?> CreateJoinLinkAsync(Guid groupId, string groupName,
        CancellationToken ct = default)
    {
        GroupJoinLinkResponse? link = null;

        var done = await errors.TryAsync(async () =>
        {
            link = await groups.CreateGroupJoinLinkAsync(groupId, ct);

            // The old link, if there was one, stopped working the moment this one was made,
            // so the message says the part somebody could otherwise be caught out by.
            snackbar.Add(UiText.Format($"New join link for {groupName}. Any link you shared before has stopped working."),
                Severity.Success);

            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not make a join link."));

        return done ? link : null;
    }

    public Task<bool> RevokeJoinLinkAsync(Guid groupId, string groupName, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.RevokeGroupJoinLinksAsync(groupId, ct);
            snackbar.Add(UiText.Format($"The join link to {groupName} no longer works."), Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not withdraw the join link."));

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
                    ? UiText.Format($"You are already in {joined.GroupName}.")
                    : UiText.Format($"You have joined {joined.GroupName}."),
                joined.AlreadyAMember ? Severity.Info : Severity.Success);

            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not join the group."));

        return done ? joined : null;
    }

    /// <remarks>
    /// Captured rather than tried, for the one refusal worth rewording. "Not settled" is the
    /// same code for leaving and for removing, so the table's sentence can only state the
    /// rule; here the person is named and the refusal carries their balance, which turns it
    /// into the thing to do next -- for a guest, a repayment a member has to record.
    /// </remarks>
    public async Task<bool> RemoveMemberAsync(Guid groupId, Guid memberUserId, string memberName,
        CancellationToken ct = default)
    {
        var error = await errors.CaptureAsync(async () =>
        {
            await groups.RemoveGroupMemberAsync(groupId, memberUserId, ct);
            snackbar.Add(UiText.Format($"{memberName} removed from the group."), Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        });

        if (error is null)
            return true;

        if (error.Code == ErrorCodes.GroupMemberNotSettled &&
            error.Extension<decimal?>("balance") is { } balance and not 0m)
        {
            // Stays until dismissed, like every other refusal: it is the answer to what
            // somebody just pressed, and it carries a figure they may want to go and check.
            snackbar.Add(UiText.Format($"Could not remove {memberName}. {ErrorMessages.NotSquare(memberName, balance)}"),
                Severity.Error);
            return false;
        }

        await errors.ShowAsync(error, UiText.Format($"Could not remove {memberName}."));
        return false;
    }

    public Task<bool> LeaveAsync(Guid groupId, string groupName, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.LeaveGroupAsync(groupId, ct);
            snackbar.Add(UiText.Format($"You have left {groupName}."), Severity.Success);
            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not leave the group."));

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
                    ? UiText.Format($"{groupName} archived. Only you stop seeing it in your list.")
                    : UiText.Format($"{groupName} is back in your list."),
                Severity.Success);

            await changes.NotifyGroupsChangedAsync();
        }, archived ? UiText.Get("Could not archive the group.") : UiText.Get("Could not unarchive the group."));

    public Task<bool> SettleAsync(Guid groupId, SettleRequest request, string otherName,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.SettleGroupDebtsAsync(groupId, request, ct);

            snackbar.Add(
                request.Direction is SettlementDirection.YouPaidThem
                    ? UiText.Format($"Recorded: you paid {otherName} {request.Amount.ToMoney()}.")
                    : UiText.Format($"Recorded: {otherName} paid you {request.Amount.ToMoney()}."),
                Severity.Success);

            // A transfer moves both balances, and it is a transaction, so it is the
            // transaction announcement the group page listens to for its figures.
            await changes.NotifyTransactionsChangedAsync();
        }, UiText.Get("Could not record the settlement."));

    public Task<bool> RecordRepaymentAsync(Guid groupId, RecordRepaymentRequest request, string fromName,
        string toName, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await groups.RecordRepaymentAsync(groupId, request, ct);

            // Both names, because the caller is on neither end: "you paid" would be wrong,
            // and a receipt that left out who is the one somebody could not check.
            snackbar.Add(UiText.Format($"Recorded: {fromName} paid {toName} {request.Amount.ToMoney()}."), Severity.Success);

            // A transfer like any other, so the same announcement settling makes.
            await changes.NotifyTransactionsChangedAsync();
        }, UiText.Get("Could not record the repayment."));

    public async Task<SettleUpResponse?> SettleUpAsync(Guid groupId, SettleUpRequest request,
        CancellationToken ct = default)
    {
        SettleUpResponse? settled = null;

        var done = await errors.TryAsync(async () =>
        {
            settled = await groups.SettleUpAsync(groupId, request, ct);

            snackbar.Add(
                UiText.Format($"Settled up: {settled.Payments.Count} ")
                + $"{(settled.Payments.Count is 1 ? "repayment" : "repayments")}, "
                + UiText.Format($"{settled.Total.ToMoney()} in all."),
                Severity.Success);

            // Transfers are transactions, so this is the announcement the group page's
            // figures listen to -- the same one recording a single repayment makes.
            await changes.NotifyTransactionsChangedAsync();
        }, UiText.Get("Could not settle up."));

        return done ? settled : null;
    }

    /// <summary>
    /// Claims a guest's link: joins the group in the guest's place.
    /// </summary>
    /// <remarks>
    /// The second message is the whole reason this reads the answer. Claiming is not only
    /// joining: whatever the group had recorded for that guest -- shares, and anything they
    /// were down as having paid for -- is the claimer's from this moment. No amount changes,
    /// and their balance in the group does, so it is said rather than left to be noticed on
    /// the next screen.
    /// </remarks>
    public async Task<InvitationClaimedResponse?> ClaimInvitationAsync(string token,
        CancellationToken ct = default)
    {
        InvitationClaimedResponse? claimed = null;

        var done = await errors.TryAsync(async () =>
        {
            claimed = await invitations.ClaimInvitationAsync(token, ct);

            snackbar.Add(Joined(claimed), Severity.Success);

            if (claimed.SharesTaken > 0 || claimed.PaymentsTaken > 0)
            {
                snackbar.Add(
                    UiText.Format($"{claimed.SharesTaken} share(s) recorded for {claimed.Name} are yours now. No amounts changed."),
                    Severity.Info);
            }

            // Said separately because it is about the future and the sentence above is
            // about the past. The shares are amounts already recorded; a rule place goes on
            // giving them a share of every expense filed under that category, which is the
            // part of a claim somebody would be most surprised to discover later.
            if (claimed.TookOnARule)
            {
                snackbar.Add(
                    claimed.RulesTaken == 1
                        ? UiText.Format($"One split rule in {claimed.GroupName} names you now, so it will give you a share of what gets filed under it.")
                        : UiText.Format($"{claimed.RulesTaken} split rules in {claimed.GroupName} name you now, so they will give you a share of what gets filed under them."),
                    Severity.Info);
            }

            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not join the group."));

        return done ? claimed : null;
    }

    /// <summary>
    /// The sentence a claim lands with, saying so when repayments disappeared in it.
    /// </summary>
    /// <remarks>
    /// Repayments between the guest and the claimer would read as the claimer paying
    /// themselves once the two are one person, so the API deletes them. Their balance is
    /// what it was -- each pair netted to nothing for them -- but rows they may remember
    /// recording have gone from the ledger, and that is said in the same breath as joining
    /// rather than discovered later.
    /// </remarks>
    private static string Joined(InvitationClaimedResponse claimed)
    {
        var joined = UiText.Format($"You have joined {claimed.GroupName} as {claimed.Name}.");

        return claimed.SettlementsCancelled switch
        {
            0 => joined,
            1 => UiText.Format($"{joined} 1 repayment between you and {claimed.Name} cancelled out."),
            var count => UiText.Format($"{joined} {count} repayments between you and {claimed.Name} cancelled out.")
        };
    }

    /// <summary>
    /// Says a guest's link is not for whoever opened it.
    /// </summary>
    /// <remarks>
    /// Whoever declines is entitled to know what became of the person the link was for, even
    /// though it is not theirs to sort out: nothing. Only the link stops; the group still has
    /// them as a guest, with everything recorded for them, and can send somebody else a new
    /// link. It used to hand the guest's position to a member, which is why there was a
    /// second sentence here and a warning about emptied rules; neither can happen now.
    /// <para>
    /// The names come from the answer rather than from the page, so the sentence is about
    /// the guest the API actually closed the link on.
    /// </para>
    /// </remarks>
    public Task<bool> DeclineInvitationAsync(string token, string groupName,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            var closed = await invitations.DeclineInvitationAsync(token, ct);

            var group = string.IsNullOrWhiteSpace(closed.GroupName) ? groupName : closed.GroupName;

            snackbar.Add(UiText.Format($"{group} still has {closed.Name} as a guest. The link no longer works."),
                Severity.Success);

            await changes.NotifyGroupsChangedAsync();
        }, UiText.Get("Could not turn the link down."));
}
