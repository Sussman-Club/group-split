using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
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
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Transactions");

            group.MapGetAll();
            group.MapGetSummary();
            group.MapGetById();
            group.MapCreate();
            group.MapUpdate();
            group.MapDelete();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        // The three parameter records are bound separately rather than gathered into one:
        // [AsParameters] does not recurse, so a record holding a PageRequest would have the
        // inner one read as a body.
        private RouteHandlerBuilder MapGetAll()
        {
            return group.MapGet(string.Empty, async (
                    [AsParameters] TransactionFilter filter,
                    [AsParameters] SortRequest sort,
                    [AsParameters] PageRequest page,
                    ICurrentUser currentUser,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var user = currentUser.User;
                    var transactions = await transactionService.List(ct);
                    return Results.Ok(await transactions
                        .Where(x => x.User.Id == user.Id)
                        .ToTransactionPageAsync(filter, sort, page, ct));
                })
                .WithName("GetTransactions")
                .Produces<PagedResponse<TransactionResponse>>()
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }

        /// <summary>
        /// What the same filter adds up to over every match, not just the page in hand: a
        /// page of 25 beside a total of its own 25 would be a lie the moment there are 26.
        /// </summary>
        private RouteHandlerBuilder MapGetSummary()
        {
            return group.MapGet("summary", async (
                    [AsParameters] TransactionFilter filter,
                    ICurrentUser currentUser,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var user = currentUser.User;
                    var transactions = await transactionService.List(ct);
                    return Results.Ok(await transactions
                        .Where(x => x.User.Id == user.Id)
                        .ToSummaryAsync(filter, ct));
                })
                .WithName("GetTransactionsSummary")
                .Produces<TransactionSummaryResponse>();
        }

        private RouteHandlerBuilder MapGetById()
        {
            return group.MapGet("{id:guid}", async (
                    Guid id,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var details = await transactionService.GetDetails(id, ct);
                    return details is not null
                        ? Results.Ok(details)
                        : Problems.NotFound(ErrorCodes.TransactionNotFound, "Transaction not found.");
                })
                .WithName("GetTransaction")
                .Produces<TransactionDetailsResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
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
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
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

                    if (transactionUpdateRequest is null)
                        return Problems.NotFound(ErrorCodes.TransactionNotFound, "Transaction not found.");

                    // Read before it is applied, because applying it destroys the
                    // difference: a patch that said nothing about the shares and one that
                    // set them to what they already were produce the same model, and they
                    // mean opposite things. Silence means "divide it again the way the
                    // category says", which is what an edit to the amount, the payer or the
                    // category should do; naming them means those amounts, checked against
                    // the (possibly also patched) total.
                    //
                    // Only a patch that says something about them gets them filled in, and
                    // it needs that: "replace /splits/0/amount" has to have an index 0 to
                    // replace. The model arrives without them precisely so that silence
                    // stays silent.
                    if (patchDocument.Touches("/splits"))
                    {
                        var current = await transactionService.GetDetails(id, ct);

                        transactionUpdateRequest.Splits = current is null
                            ? []
                            : [.. current.Splits.Select(split =>
                                new SplitInput { UserId = split.UserId, Amount = split.Amount })];
                    }

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
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
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
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }

    extension(IQueryable<Expense> transactions)
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
                    GroupId = transaction.Group!.Id,
                    GroupName = transaction.Group!.Name,
                    PaidByUserId = transaction.User.Id,
                    PaidByUserName = transaction.User.FirstName +
                                     (transaction.User.LastName != null ? " " + transaction.User.LastName : ""),
                    CategoryId = transaction.CategoryId,
                    Category = transaction.Category != null ? transaction.Category.Name : null
                };
        }

        internal IQueryable<Expense> ApplyFilter(TransactionFilter? filter)
        {
            if (filter is null)
                return transactions;

            // Hoisted out of the expression, and normalised while they are here. Npgsql
            // writes a DateTimeOffset to a timestamptz column only at offset zero, so a
            // "+02:00" from a client has to become UTC before it reaches the parameter;
            // lowering the two strings once keeps the comparison off the database's collation.
            // Not named from/to: inside a query expression those are keywords.
            var after = filter.From?.ToUniversalTime();
            var before = filter.To?.ToUniversalTime();
            var category = filter.Category?.Trim().ToLowerInvariant();
            var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim().ToLowerInvariant();

            return from transaction in transactions
                where (after == null || transaction.DateTime >= after) &&
                      (before == null || transaction.DateTime <= before) &&
                      (filter.GroupId == null || transaction.GroupId == filter.GroupId) &&
                      (filter.PaidByUserId == null || transaction.User.Id == filter.PaidByUserId) &&
                      (category == null ||
                       (transaction.Category != null && transaction.Category.Name.ToLower() == category)) &&
                      // ToLower().Contains rather than EF.Functions.ILike: the same query has
                      // to run on Npgsql and on the in-memory provider the tests use, and
                      // ILike translates only on the first. The names are nullable once an
                      // account has been anonymised.
                      (search == null ||
                       transaction.Name.ToLower().Contains(search) ||
                       (transaction.Description != null && transaction.Description.ToLower().Contains(search)) ||
                       (transaction.Category != null &&
                        transaction.Category.Name.ToLower().Contains(search)) ||
                       transaction.Group!.Name.ToLower().Contains(search) ||
                       (transaction.User.FirstName != null && transaction.User.FirstName.ToLower().Contains(search)) ||
                       (transaction.User.LastName != null && transaction.User.LastName.ToLower().Contains(search)))
                select transaction;
        }

        /// <summary>
        /// The filter, the order and the page, in that order. Both listings go through here
        /// so neither can drift from the other, or from the summary below.
        /// </summary>
        internal Task<PagedResponse<TransactionResponse>> ToTransactionPageAsync(
            TransactionFilter filter, SortRequest sort, PageRequest page, CancellationToken ct) =>
            transactions.ApplyFilter(filter).ApplySort(sort, Sort).SelectDto().ToPageAsync(page, ct);

        /// <summary>The same filter, counted and totalled instead of paged.</summary>
        internal async Task<TransactionSummaryResponse> ToSummaryAsync(
            TransactionFilter filter, CancellationToken ct)
        {
            var matches = transactions.ApplyFilter(filter);

            return new TransactionSummaryResponse(
                await matches.CountAsync(ct),
                await matches.SumAsync(transaction => transaction.Amount, ct));
        }
    }

    /// <summary>
    /// The orders an expense listing offers. Applied to the entity rather than the response,
    /// so a key can reach through a navigation to the group or the payer.
    /// </summary>
    internal static readonly SortMap<Expense> Sort = new SortMap<Expense>()
        .Key("dateTime", transaction => transaction.DateTime, defaultDescending: true)
        .Key("amount", transaction => transaction.Amount, defaultDescending: true)
        .Key("name", transaction => transaction.Name)
        .Key("category", transaction => transaction.Category!.Name)
        .Key("group", transaction => transaction.Group!.Name)
        .Key("paidBy", transaction => transaction.User.FirstName)
        .Default("dateTime")
        .TieBreak(transaction => transaction.Id);
}
