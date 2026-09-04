using GroupSplit.API.Extensions;
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
            group.MapGetById();
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
                    ICurrentUser currentUser,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var user = currentUser.User;
                    var transactions = await transactionService.List(ct);
                    var transactionResponses = await transactions
                        .Where(x => x.User.Id == user.Id)
                        .ApplyFilter(filter).SelectDto().ToListAsync(ct);
                    return Results.Ok(transactionResponses);
                })
                .WithName("GetTransactions")
                .Produces<TransactionResponse[]>();
        }
        
        private RouteHandlerBuilder MapGetById()
        {
            return group.MapGet("{id:guid}", async (
                    Guid id,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var details = await transactionService.GetDetails(id, ct);
                    return details is not null ? Results.Ok(details) : Results.NotFound();
                })
                .WithName("GetTransaction")
                .Produces<TransactionDetailsResponse>()
                .Produces(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapCreate()
        {
            return group.MapPost(string.Empty, async (
                    CreateTransactionRequest request,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var transaction = await transactionService.Create(request, ct);
                    var transactions = await transactionService.Get(transaction.Id, ct);
                    var transactionResponse = await transactions.SelectDto().FirstOrDefaultAsync(ct);
                    return Results.Ok(transactionResponse);
                })
                .WithName("CreateTransaction")
                .Produces<TransactionResponse>()
                .Produces(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapUpdate()
        {
            return group.MapPatch("{id:guid}", async (
                    Guid id,
                    JsonPatchDocument<UpdateTransactionRequest> patchDocument,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var transactionUpdateRequest = await transactionService.GetUpdateModel(id, ct);

                    if (transactionUpdateRequest is null) return Results.NotFound();

                    patchDocument.ApplyTo(transactionUpdateRequest);

                    if (!PatchedModel.IsValid(transactionUpdateRequest, out var invalid))
                        return invalid;

                    await transactionService.Update(id, transactionUpdateRequest, ct);

                    var transactions = await transactionService.Get(id, ct);

                    var transactionResponse = await transactions.SelectDto().FirstOrDefaultAsync(ct);

                    return Results.Ok(transactionResponse);
                })
                .WithName("UpdateTransaction")
                .Produces<TransactionResponse>()
                .Produces(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapDelete()
        {
            return group.MapDelete("{id:guid}",
                    async (Guid id, ITransactionService transactionService, CancellationToken ct) =>
                    {
                        await transactionService.Delete(id, ct);
                        return Results.Ok();
                    }
                )
                .WithName("DeleteTransaction")
                .Produces(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status404NotFound);
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
                    DateTime = transaction.DateTime,
                    Name = transaction.Name,
                    Description = transaction.Description,
                    GroupId = transaction.RuleVersion.Rule.Group.Id,
                    GroupName = transaction.RuleVersion.Rule.Group.Name,
                    PaidByUserId = transaction.User.Id,
                    PaidByUserName = transaction.User.FirstName +
                                     (transaction.User.LastName != null ? " " + transaction.User.LastName : ""),
                    RuleVersionId = transaction.RuleVersion.Id,
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
