using GroupSplit.API.Services;
using GroupSplit.Shared;
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

        var userServiceScoped = scope.ServiceProvider.GetRequiredService<IUserService>();
        var transactionServiceScoped = scope.ServiceProvider.GetRequiredService<ITransactionService>();

        var otherUser = await userServiceScoped.GetCurrentUser();

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
}