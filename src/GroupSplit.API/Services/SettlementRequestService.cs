using System.Security.Cryptography;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ISettlementRequestService
{
    Task<SettlementRequestResponse> CreateForGroup(Guid groupId, CreateSettlementRequest request,
        CancellationToken cancellationToken = default);
    Task<SettlementRequestResponse> CreateAcrossGroups(CreateSettlementRequest request,
        CancellationToken cancellationToken = default);
    Task<SettlementRequestResponse> Get(string token, CancellationToken cancellationToken = default);
    Task<SettlementRequestResponse> Cancel(string token, CancellationToken cancellationToken = default);
    Task<SettlementRequestResponse> RecordRepayment(string token, CancellationToken cancellationToken = default);
    Task ResolveMatching(Guid fromUserId, Guid toUserId, IReadOnlyList<GroupDebt> payments,
        CancellationToken cancellationToken = default);
}

/// <summary>Coordinates settlement requests without ever treating one as a payment.</summary>
public class SettlementRequestService(
    ICurrentUser currentUser,
    IGroupService groupService,
    IDebtCalculationService debts,
    AppDbContext context,
    TimeProvider clock) : ISettlementRequestService
{
    private const int TokenBytes = 32;

    public async Task<SettlementRequestResponse> CreateForGroup(Guid groupId, CreateSettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await (await groupService.GetGroupById(groupId, cancellationToken))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        var position = await debts.GetUserBalance(
            await (await groupService.GetGroupNetBalance(groupId, cancellationToken)).ToListAsync(cancellationToken));
        var debt = position.OwedToYou.SingleOrDefault(item => item.UserId == request.RecipientUserId)
            ?? throw NothingToRequest();

        return await Create(request, [(group, debt.Amount)], cancellationToken);
    }

    public async Task<SettlementRequestResponse> CreateAcrossGroups(CreateSettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        var groups = await (await groupService.GetAllGroups(cancellationToken))
            .Where(group => group.Id != currentUser.User.Id)
            .ToListAsync(cancellationToken);

        var represented = new List<(Group Group, decimal Debt)>();
        foreach (var group in groups)
        {
            var position = await debts.GetUserBalance(
                await (await groupService.GetGroupNetBalance(group.Id, cancellationToken)).ToListAsync(cancellationToken));
            var debt = position.OwedToYou.FirstOrDefault(item => item.UserId == request.RecipientUserId);
            if (debt is not null)
                represented.Add((group, debt.Amount));
        }

        return await Create(request, represented, cancellationToken);
    }

    public async Task<SettlementRequestResponse> Get(string token, CancellationToken cancellationToken = default)
    {
        var settlementRequest = await Find(token, cancellationToken);
        if (settlementRequest.RecipientUserId != currentUser.User.Id)
            throw new ForbiddenException(ErrorCodes.Forbidden, "Only the named recipient can open this settlement request.");

        await MarkStaleIfChanged(settlementRequest, cancellationToken);
        return ToResponse(settlementRequest);
    }

    public async Task<SettlementRequestResponse> Cancel(string token, CancellationToken cancellationToken = default)
    {
        var settlementRequest = await Find(token, cancellationToken);
        if (settlementRequest.RequesterUserId != currentUser.User.Id)
            throw new ForbiddenException(ErrorCodes.Forbidden, "Only the requester can cancel this settlement request.");

        await MarkStaleIfChanged(settlementRequest, cancellationToken);
        EnsureOpen(settlementRequest);
        settlementRequest.CancelledAt = clock.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
        return ToResponse(settlementRequest);
    }

    public async Task<SettlementRequestResponse> RecordRepayment(string token,
        CancellationToken cancellationToken = default)
    {
        var settlementRequest = await Find(token, cancellationToken);
        if (settlementRequest.RecipientUserId != currentUser.User.Id)
            throw new ForbiddenException(ErrorCodes.Forbidden, "Only the named recipient can record this repayment.");

        await MarkStaleIfChanged(settlementRequest, cancellationToken);
        EnsureOpen(settlementRequest);

        var requester = await context.Set<User>().SingleAsync(user => user.Id == settlementRequest.RequesterUserId,
            cancellationToken);
        var recipient = currentUser.User;
        var groupIds = settlementRequest.Groups.Select(part => part.GroupId).ToList();
        var groups = await context.Set<Group>().Include(group => group.Users)
            .Where(group => groupIds.Contains(group.Id)).ToDictionaryAsync(group => group.Id, cancellationToken);
        var now = clock.GetUtcNow();

        foreach (var part in settlementRequest.Groups)
        {
            var group = groups[part.GroupId];
            if (!group.Users.Any(user => user.Id == requester.Id) || !group.Users.Any(user => user.Id == recipient.Id))
                throw new ConflictException(ErrorCodes.SettlementRequestNotOpen,
                    "This settlement request no longer names two active group members.");
            context.Add(group.SettlementBetween(recipient, requester, part.RequestedAmount, now,
                "Settlement request"));
        }

        settlementRequest.ResolvedAt = now;
        await context.SaveChangesAsync(cancellationToken);
        return ToResponse(settlementRequest);
    }

    public async Task ResolveMatching(Guid fromUserId, Guid toUserId, IReadOnlyList<GroupDebt> payments,
        CancellationToken cancellationToken = default)
    {
        var candidates = await context.Set<SettlementRequest>().Include(request => request.Groups)
            .Where(request => request.RecipientUserId == fromUserId && request.RequesterUserId == toUserId &&
                              request.CancelledAt == null && request.ResolvedAt == null && request.StaleAt == null)
            .ToListAsync(cancellationToken);
        var matching = candidates.Where(request => request.Groups.Count == payments.Count &&
            request.Groups.All(part => payments.Any(payment => payment.GroupId == part.GroupId &&
                payment.Amount == part.RequestedAmount))).ToList();
        if (matching.Count == 0)
            return;
        var now = clock.GetUtcNow();
        foreach (var request in matching)
            request.ResolvedAt = now;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<SettlementRequestResponse> Create(CreateSettlementRequest request,
        IReadOnlyList<(Group Group, decimal Debt)> represented, CancellationToken cancellationToken)
    {
        if (request.RecipientUserId == currentUser.User.Id)
            throw new ConflictException(ErrorCodes.SettlementWithSelf, "A settlement needs two different people.");
        if (request.Amount <= 0)
            throw new ConflictException(ErrorCodes.SettlementAmountNotPositive,
                "A settlement request must be for more than zero.");
        if (represented.Count == 0)
            throw NothingToRequest();

        if (represented.Select(item => item.Group.Currency).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new ConflictException(ErrorCodes.CurrencyMismatch,
                "A settlement request can only cover groups with one currency.");

        var recipient = await context.Set<User>().SingleOrDefaultAsync(user => user.Id == request.RecipientUserId,
            cancellationToken) ?? throw new NotFoundException(ErrorCodes.UserNotFound, "That person was not found.");
        var groupIds = represented.Select(item => item.Group.Id).ToList();
        var recipientIsMember = await context.Set<User>()
            .Where(user => user.Id == recipient.Id)
            .Select(user => user.Groups.Count(group => groupIds.Contains(group.Id)))
            .SingleAsync(cancellationToken);
        if (recipientIsMember != groupIds.Count)
            throw NothingToRequest();
        var remaining = request.Amount;
        var parts = represented.OrderByDescending(item => item.Debt).Select(item =>
        {
            var amount = Math.Min(remaining, item.Debt);
            remaining -= amount;
            return new SettlementRequestGroup { GroupId = item.Group.Id, DebtAtCreation = item.Debt, RequestedAmount = amount };
        }).Where(part => part.RequestedAmount > 0).ToList();

        // A request is an invitation to record an outside payment, not a balance edit. The
        // requester may be settling a period whose amount exceeds today's running debt, so
        // any remainder belongs to the largest represented group just as an ordinary
        // cross-group repayment does.
        if (remaining > 0)
            parts[0].RequestedAmount += remaining;

        var created = new SettlementRequest
        {
            Requester = currentUser.User,
            Recipient = recipient,
            Token = NewToken(),
            Currency = represented[0].Group.Currency,
            Amount = request.Amount,
            CreatedAt = clock.GetUtcNow()
        };
        foreach (var part in parts)
            created.Groups.Add(part);
        context.Add(created);
        await context.SaveChangesAsync(cancellationToken);
        return ToResponse(created);
    }

    private async Task<SettlementRequest> Find(string token, CancellationToken cancellationToken) =>
        await context.Set<SettlementRequest>().Include(request => request.Requester).Include(request => request.Recipient)
            .Include(request => request.Groups).ThenInclude(part => part.Group)
            .SingleOrDefaultAsync(request => request.Token == token, cancellationToken)
        ?? throw new NotFoundException(ErrorCodes.SettlementRequestNotFound, "Settlement request was not found.");

    private async Task MarkStaleIfChanged(SettlementRequest request, CancellationToken cancellationToken)
    {
        if (request.CancelledAt is not null || request.ResolvedAt is not null || request.StaleAt is not null)
            return;

        foreach (var part in request.Groups)
        {
            var position = await debts.GetUserBalance(await (await groupService.GetGroupNetBalance(part.GroupId,
                cancellationToken)).ToListAsync(cancellationToken));
            var current = position.YouOwed.FirstOrDefault(item => item.UserId == request.RequesterUserId)?.Amount ?? 0m;
            if (current == part.DebtAtCreation)
                continue;
            request.StaleAt = clock.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken);
            return;
        }
    }

    private static void EnsureOpen(SettlementRequest request)
    {
        if (request.CancelledAt is not null || request.ResolvedAt is not null || request.StaleAt is not null)
            throw new ConflictException(ErrorCodes.SettlementRequestNotOpen,
                "This settlement request is no longer open.");
    }

    private static ConflictException NothingToRequest() => new(ErrorCodes.SettlementNothingToSettle,
        "There is nothing outstanding between the two of you in that direction.");

    private static SettlementRequestResponse ToResponse(SettlementRequest request) => new(
        request.Id, request.Token,
        request.ResolvedAt is not null ? SettlementRequestStatus.Paid : request.CancelledAt is not null
            ? SettlementRequestStatus.Cancelled : request.StaleAt is not null ? SettlementRequestStatus.Stale
            : SettlementRequestStatus.Open,
        request.RequesterUserId, Name(request.Requester), request.RecipientUserId, Name(request.Recipient),
        request.Amount, request.Currency, request.CreatedAt,
        request.Groups.Select(part => new GroupDebt(part.GroupId, part.Group.Name, part.RequestedAmount)).ToList());

    private static string Name(User user) => $"{user.FirstName} {user.LastName}".Trim();

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
