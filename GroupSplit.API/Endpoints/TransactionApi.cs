using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
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
            group.MapCreate();
            group.MapUpdate();
            group.MapDelete();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapGetAll()
        {
            return group.MapGet(string.Empty, async (
                    [AsParameters] TransactionFilter filter,
                    IUserService userService,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var user = await userService.GetCurrentUser();
                    var transactions = await transactionService.List(ct);
                    var transactionResponses = await transactions
                        .Where(x => x.User.Id == user.Id)
                        .ApplyFilter(filter).SelectDto().ToListAsync(ct);
                    return Results.Ok(transactionResponses);
                })
                .WithName("GetTransactions")
                .Produces<TransactionResponse[]>();
        }

        private RouteHandlerBuilder MapCreate()
        {
            return group.MapPost(string.Empty, async (
                    CreateTransactionRequest request,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var transaction = await transactionService.Create(request, ct);
                    return Results.Ok();
                })
                .WithName("CreateTransaction");
        }

        private RouteHandlerBuilder MapUpdate()
        {
            return group.MapPatch("{id:guid}", async (
                Guid id,
                JsonPatchDocument<UpdateTransactionRequest> patchDocument,
                ITransactionService transactionService,
                CancellationToken ct) =>
            {
                var transactions = await transactionService.Get(id, ct);
                var transactionUpdateRequest = await (from t in transactions
                    select new UpdateTransactionRequest
                    {
                        Amount = t.Amount,
                        Description = t.Description,
                        Name = t.Name,
                        DateTime = t.DateTime,
                        PaidByUserId = t.User.Id,
                        RuleVersionId = t.RuleVersion.Id
                    }).FirstOrDefaultAsync(ct);

                if (transactionUpdateRequest is null) return Results.NotFound();

                patchDocument.ApplyTo(transactionUpdateRequest);

                await transactionService.Update(id, transactionUpdateRequest, ct);

                return Results.Ok();
            });
        }

        private RouteHandlerBuilder MapDelete()
        {
            return group.MapDelete("{id:guid}",
                async (Guid id, ITransactionService transactionService, CancellationToken ct) =>
                {
                    await transactionService.Delete(id, ct);
                    return Results.Ok();
                }
            );
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
                    Category = transaction.RuleVersion.Rule.Category
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