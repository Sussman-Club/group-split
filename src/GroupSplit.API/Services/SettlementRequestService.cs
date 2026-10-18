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
    Task<SettlementRequestResponse> Get(string token, CancellationToken cancellationToken = default);
    Task<SettlementRequestResponse> Cancel(string token, CancellationToken cancellationToken = default);
    Task<SettlementRequestResponse> RecordRepayment(string token, CancellationToken cancellationToken = default);
}

/// <summary>Coordinates settlement requests without ever treating one as a payment.</summary>
public class SettlementRequestService(
    ICurrentUser currentUser,
    IGroupService groupService,
    ISettlementService settlements,
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

        var position = await settlements.GetPosition(groupId, Cutoff.Of(request.Through, request.UtcOffset), cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");
        var debt = position.OwedToYou.SingleOrDefault(item => item.UserId == request.RecipientUserId)
            ?? throw NothingToRequest();

        return await Create(request, group, debt.Amount, cancellationToken);
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

        if (settlementRequest.ResolvedAt is not null || settlementRequest.CancelledAt is not null)
            EnsureOpen(settlementRequest);
        settlementRequest.CancelledAt = clock.GetUtcNow();
        await SaveRequest(cancellationToken);
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
                "Settlement request", settlementRequest.Through));
        }

        settlementRequest.ResolvedAt = now;
        await SaveRequest(cancellationToken);
        return ToResponse(settlementRequest);
    }

    private async Task<SettlementRequestResponse> Create(CreateSettlementRequest request,
        Group group, decimal debt, CancellationToken cancellationToken)
    {
        if (request.RecipientUserId == currentUser.User.Id)
            throw new ConflictException(ErrorCodes.SettlementWithSelf, "A settlement needs two different people.");
        if (request.Amount <= 0)
            throw new ConflictException(ErrorCodes.SettlementAmountNotPositive,
                "A settlement request must be for more than zero.");
        if (decimal.Round(request.Amount, 2) != request.Amount)
            throw new ValidationException(ErrorCodes.ValidationFailed, "Use no more than two decimal places.");
        var recipient = await context.Set<User>().SingleOrDefaultAsync(user => user.Id == request.RecipientUserId,
            cancellationToken) ?? throw new NotFoundException(ErrorCodes.UserNotFound, "That person was not found.");
        var recipientIsMember = await context.Set<User>()
            .Where(user => user.Id == recipient.Id)
            .Select(user => user.Groups.Any(candidate => candidate.Id == group.Id))
            .SingleAsync(cancellationToken);
        if (!recipientIsMember)
            throw NothingToRequest();

        var created = new SettlementRequest
        {
            Requester = currentUser.User,
            Recipient = recipient,
            Token = NewToken(),
            Currency = group.Currency,
            Amount = request.Amount,
            CreatedAt = clock.GetUtcNow(),
            Through = request.Through,
            UtcOffset = request.Through is null ? null : request.UtcOffset ?? 0
        };
        created.Groups.Add(new SettlementRequestGroup
        {
            Group = group, DebtAtCreation = debt, RequestedAmount = request.Amount
        });
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
            var position = await settlements.GetPosition(part.GroupId, Cutoff.Of(request.Through, request.UtcOffset),
                cancellationToken);
            var current = position?.Plan.FirstOrDefault(payment => payment.FromUserId == request.RecipientUserId &&
                payment.ToUserId == request.RequesterUserId)?.Amount ?? 0m;
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

    private async Task SaveRequest(CancellationToken ct)
    {
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ErrorCodes.SettlementRequestNotOpen,
                "This settlement request was already changed. Refresh to see its status.");
        }
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
        request.Groups.Select(part => new GroupDebt(part.GroupId, part.Group.Name, part.RequestedAmount)).ToList(),
        request.Through);

    private static string Name(User user) => $"{user.FirstName} {user.LastName}".Trim();

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
