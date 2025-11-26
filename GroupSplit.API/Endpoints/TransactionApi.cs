using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

public static class TransactionApi
{
    extension(IEndpointRouteBuilder routeBuilder)
    {
        public RouteGroupBuilder MapTransaction()
        {
            var group = routeBuilder
                .MapGroup("/transactions")
                .RequireAuthorization();

            group.WithTags("Transactions");

            group.MapGetAll();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapGetAll()
        {
            return group.MapGet(string.Empty, async (
                    [AsParameters] TransactionFilter filter,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var transactions = await transactionService.GetAll(ct);
                    var groupInfos = await transactions.ApplyFilter(filter).SelectDto().ToListAsync(ct);
                    return Results.Ok(groupInfos);
                })
                .WithName("GetTransactions")
                .Produces<TransactionResponse[]>();
        }
    }

    extension(IQueryable<Transaction> transactions)
    {
        internal IQueryable<TransactionResponse> SelectDto()
        {
            return from transaction in transactions
                select new TransactionResponse
                {
                    Id = transaction.Id,
                    Amount = transaction.Amount,
                    Date = transaction.DateTime,
                    Name = transaction.Name,
                    Description = transaction.Description,
                    GroupName = transaction.RuleVersion.Rule.Group.Name,
                    PaidByName = transaction.User.FirstName +
                                 (transaction.User.LastName != null ? " " + transaction.User.LastName : ""),
                };
        }

        internal IQueryable<Transaction> ApplyFilter(TransactionFilter? filter)
        {
            if (filter is null)
                return transactions;

            return from transaction in transactions
                where (filter.From == null || transaction.DateTime >= filter.From) &&
                      (filter.To == null || transaction.DateTime <= filter.To)
                select transaction;
        }
    }
}