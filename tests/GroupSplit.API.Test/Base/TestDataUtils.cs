using GroupSplit.API.Services;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Base;

public static class TestDataUtils
{
    public static async Task<Data.Entities.Transaction> CreateTransactionForNewUserAsync(
        IServiceProvider serviceProvider,
        string name = "Other User Tx",
        decimal amount = 10.00m)
    {
        await using var scope = serviceProvider.CreateAsyncScope();

        await ApiUnitTest.InitializeCurrentUser(scope.ServiceProvider);
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        var transactionServiceScoped = scope.ServiceProvider.GetRequiredService<ITransactionService>();

        var otherUser = currentUser.User;

        var request = new CreateTransactionRequest
        {
            Name = name,
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = otherUser.Id
        };

        return await transactionServiceScoped.Create(
            request,
            TestContext.Current.CancellationToken
        );
    }

    /// <summary>
    /// A settlement between two people the caller has nothing to do with, in a group the
    /// caller is not in.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="IGroupService.Settle"/> in its own scope, so the transfer
    /// is shaped exactly as a real settlement is -- one row, one split -- rather than by a
    /// test assembling the entity and getting to choose the invariants it holds.
    /// </remarks>
    public static async Task<Data.Entities.Transfer> CreateTransferForStrangersAsync(
        IServiceProvider serviceProvider,
        decimal amount = 25.00m)
    {
        await using var scope = serviceProvider.CreateAsyncScope();

        await ApiUnitTest.InitializeCurrentUser(scope.ServiceProvider);

        var groups = scope.ServiceProvider.GetRequiredService<IGroupService>();
        var users = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        var context = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();

        var group = await groups.CreateGroup(
            new CreateGroupRequest { Name = "Strangers" },
            TestContext.Current.CancellationToken);

        var other = await CreateUserAsync(serviceProvider);

        // Joined here rather than through the invitation flow, for the reason JoinGroup
        // gives: this is scenery, and the flow has its own tests.
        var tracked = await context.Set<Data.Entities.Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == group.Id, TestContext.Current.CancellationToken);

        var member = await context.Set<Data.Entities.User>()
            .FirstAsync(candidate => candidate.Id == other.Id, TestContext.Current.CancellationToken);

        tracked.Users.Add(member);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The in-memory provider does not apply the store default, so without this the
        // membership carries the zero date.
        var membership = await context.Set<Data.Entities.GroupMembership>()
            .FirstAsync(row => row.GroupId == group.Id && row.UserId == other.Id,
                TestContext.Current.CancellationToken);

        membership.JoinedAt = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await groups.Settle(
            group.Id,
            new SettleRequest { UserId = other.Id, Amount = amount },
            TestContext.Current.CancellationToken);

        return await context.Set<Data.Entities.Transfer>()
            .Where(transfer => transfer.GroupId == group.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Data.Entities.User> CreateUserAsync(IServiceProvider serviceProvider)
    {
        await using var scope = serviceProvider.CreateAsyncScope();

        await ApiUnitTest.InitializeCurrentUser(scope.ServiceProvider);

        return scope.ServiceProvider.GetRequiredService<ICurrentUser>().User;
    }
}
