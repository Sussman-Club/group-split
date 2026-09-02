using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.AppHost.Test.Data;

public class DataTest(AppHostFixture appHost) : IAsyncLifetime
{
    protected IDbContextFactory<AppDbContext> ContextFactory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await appHost.SeedAsync();
        ContextFactory = await appHost.GetDbContextFactory();
    }

    [Fact]
    public async Task TestDateTimeWithOffset()
    {
        await using var context = await ContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // This will roll-back the changes so the test does not change anything.
        await using var dbTransaction =
            await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        
        var queryResult = await (from user in context.Set<User>()
            
            select new
            {
                user,
                personalGroup = user.PersonalGroup
            }).FirstAsync(TestContext.Current.CancellationToken);

        var transaction = new Transaction
        {
            Amount = 100,
            DateTime = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8)),
            Name = "Test",
            User = queryResult.user,
            RuleVersion = new PersonalRuleVersion
            {
                Rule = new Rule
                {
                    Category = "Test",
                    Group = queryResult.personalGroup
                },
                StartDateTime = DateTime.UtcNow
            }
        };

        context.Add(transaction);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}