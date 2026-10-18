using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Member = GroupSplit.Data.Entities.User;

namespace GroupSplit.API.Test.Group;

public class SettlementRequestTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ISettlementRequestService Requests => GetService<ISettlementRequestService>();

    private async Task<(Guid GroupId, Member Other)> Debt()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Request tests" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group.Id, Name = "Dinner", Amount = 100, DateTime = DateTimeOffset.UtcNow
        }, Ct);
        return (group.Id, other);
    }

    private async Task<IServiceScope> As(Member user)
    {
        var scope = ServiceProvider.CreateScope();
        var tracked = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<Member>().SingleAsync(x=>x.Id==user.Id,Ct);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().Initialize(tracked);
        return scope;
    }

    [Fact]
    public async Task A_request_can_exceed_current_debt_without_moving_balances_and_only_recipient_can_record_it()
    {
        var (group, other) = await Debt();
        var request = await Requests.CreateForGroup(group, new CreateSettlementRequest { RecipientUserId = other.Id, Amount = 80 }, Ct);
        Assert.Empty(await DbContext.Set<Transfer>().ToListAsync(Ct));
        await Assert.ThrowsAsync<ForbiddenException>(()=>Requests.Get(request.Token,Ct));
        using var scope = await As(other);
        var recipient = scope.ServiceProvider.GetRequiredService<ISettlementRequestService>();
        Assert.Equal(SettlementRequestStatus.Open, (await recipient.Get(request.Token,Ct)).Status);
        Assert.Equal(SettlementRequestStatus.Paid, (await recipient.RecordRepayment(request.Token,Ct)).Status);
        await Assert.ThrowsAsync<ConflictException>(()=>recipient.RecordRepayment(request.Token,Ct));
        Assert.Equal(80, Assert.Single(await DbContext.Set<Transfer>().ToListAsync(Ct)).Amount);
    }

    [Fact]
    public async Task The_requester_can_cancel_without_moving_balances()
    {
        var (group, other) = await Debt();
        var request = await Requests.CreateForGroup(group, new CreateSettlementRequest { RecipientUserId = other.Id, Amount = 50 }, Ct);
        Assert.Equal(SettlementRequestStatus.Cancelled, (await Requests.Cancel(request.Token,Ct)).Status);
        using var scope = await As(other);
        var recipient = scope.ServiceProvider.GetRequiredService<ISettlementRequestService>();
        Assert.Equal(SettlementRequestStatus.Cancelled, (await recipient.Get(request.Token,Ct)).Status);
        await Assert.ThrowsAsync<ConflictException>(()=>recipient.RecordRepayment(request.Token,Ct));
        Assert.Empty(await DbContext.Set<Transfer>().ToListAsync(Ct));
    }

    [Fact]
    public async Task A_balance_change_makes_the_request_stale_and_unrecordable()
    {
        var (group, other) = await Debt();
        var request = await Requests.CreateForGroup(group, new CreateSettlementRequest { RecipientUserId = other.Id, Amount = 50 }, Ct);
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group, Name = "Another dinner", Amount = 20, DateTime = DateTimeOffset.UtcNow
        }, Ct);
        using var scope = await As(other);
        var recipient = scope.ServiceProvider.GetRequiredService<ISettlementRequestService>();
        Assert.Equal(SettlementRequestStatus.Stale, (await recipient.Get(request.Token,Ct)).Status);
        await Assert.ThrowsAsync<ConflictException>(()=>recipient.RecordRepayment(request.Token,Ct));
    }

    [Fact]
    public async Task A_period_request_uses_the_earlier_balance_and_remembers_the_date_on_repayment()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Monthly repayments" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group.Id, Name = "August", Amount = 100,
            DateTime = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)
        }, Ct);
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group.Id, Name = "September", Amount = 80, PaidByUserId = other.Id,
            DateTime = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero)
        }, Ct);
        var day = new DateOnly(2026, 8, 31);
        var request = await Requests.CreateForGroup(group.Id, new CreateSettlementRequest
        {
            RecipientUserId = other.Id, Amount = 50, Through = day, UtcOffset = -240
        }, Ct);
        Assert.Equal(day, request.Through);
        Assert.Equal(50, Assert.Single((await DbContext.Set<SettlementRequest>()
            .Include(row=>row.Groups).SingleAsync(Ct)).Groups).DebtAtCreation);
        // Later spending does not alter the requested period.
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group.Id, Name = "October", Amount = 40,
            DateTime = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
        }, Ct);
        using var scope = await As(other);
        var recipient = scope.ServiceProvider.GetRequiredService<ISettlementRequestService>();
        Assert.Equal(SettlementRequestStatus.Open, (await recipient.Get(request.Token, Ct)).Status);
        await recipient.RecordRepayment(request.Token, Ct);
        Assert.Equal(day, (await DbContext.Set<Transfer>().SingleAsync(Ct)).CoversThrough);
    }

    [Fact]
    public async Task An_ordinary_matching_repayment_resolves_the_request_in_the_same_save()
    {
        var (group, other) = await Debt();
        var request = await Requests.CreateForGroup(group, new CreateSettlementRequest { RecipientUserId = other.Id, Amount = 50 }, Ct);
        await GetService<ISettlementService>().Settle(group, new SettleRequest { UserId = other.Id, Amount = 50 }, Ct);
        using var scope = await As(other);
        Assert.Equal(SettlementRequestStatus.Paid, (await scope.ServiceProvider.GetRequiredService<ISettlementRequestService>()
            .Get(request.Token,Ct)).Status);
    }
}
