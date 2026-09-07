using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
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
            group.MapGetShares();
            group.MapGetSharesSummary();
            group.MapGetById();
            group.MapBankMatches();
            group.MapCreate();
            group.MapPreviewSplits();
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

        /// <summary>
        /// The other half of the ledger: what the caller owes a share of, expense by
        /// expense, whoever paid for it.
        /// </summary>
        /// <remarks>
        /// <c>GET /transactions</c> answers "what have I paid" and had no counterpart, so
        /// the only way to see what you owed was a per-group balance with no rows behind
        /// it. The rows have existed since splits became their own table -- one per person
        /// per transaction, indexed on the user -- and this reads them.
        /// <para>
        /// The same filter, sort and page as the expense listing, over the same expenses,
        /// so the two are two views of one set rather than two listings that happen to
        /// agree.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapGetShares()
        {
            return group.MapGet("shares", async (
                    [AsParameters] TransactionFilter filter,
                    [AsParameters] SortRequest sort,
                    [AsParameters] PageRequest page,
                    ICurrentUser currentUser,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var shares = await transactionService.Shares(ct);
                    var expenses = await transactionService.List(ct);

                    return Results.Ok(await shares
                        .ToSharePageAsync(expenses, filter, sort, page, currentUser.User.Id, ct));
                })
                .WithName("GetTransactionShares")
                .Produces<PagedResponse<ExpenseShareResponse>>()
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }

        /// <summary>
        /// What the share listing adds up to over the whole match, the way
        /// <c>GET /transactions/summary</c> does for the one beside it.
        /// </summary>
        private RouteHandlerBuilder MapGetSharesSummary()
        {
            return group.MapGet("shares/summary", async (
                    [AsParameters] TransactionFilter filter,
                    ICurrentUser currentUser,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var shares = await transactionService.Shares(ct);
                    var expenses = await transactionService.List(ct);

                    return Results.Ok(await shares
                        .ToShareSummaryAsync(expenses, filter, currentUser.User.Id, ct));
                })
                .WithName("GetTransactionSharesSummary")
                .Produces<ExpenseShareSummaryResponse>();
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

        /// <summary>
        /// Imported rows still waiting that could be this same money.
        /// </summary>
        /// <remarks>
        /// The other order of the same problem the inbox catches. Somebody records the
        /// dinner at the table, the card charge lands two days later, and by then nothing
        /// remembers the dinner was already written down -- so the moment to ask is right
        /// after an expense is recorded, while the person still knows what they paid for.
        /// <para>
        /// A read, and only a read. Attaching one of them is <c>POST /inbox/{id}/link</c>
        /// and saying they are different is <c>POST /inbox/{id}/dismiss-match</c>; both are
        /// somebody pressing something.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapBankMatches()
        {
            return group.MapGet("{id:guid}/bank-matches", async (
                    Guid id,
                    ITransactionService transactionService,
                    IDuplicateMatcher matcher,
                    IInboxService inbox,
                    CancellationToken ct) =>
                {
                    var expense = await (await transactionService.Get(id, ct)).FirstOrDefaultAsync(ct);

                    if (expense is null)
                        return Problems.NotFound(ErrorCodes.TransactionNotFound, "Transaction not found.");

                    var rows = await matcher.RowsLike(expense, ct);

                    if (rows.Count == 0)
                        return Results.Ok<IReadOnlyList<BankTransactionResponse>>([]);

                    // Through the inbox's own listing, so a suggested row is described by
                    // exactly the projection the inbox page shows -- account, institution,
                    // labels and all.
                    var ids = rows.Select(row => row.Id).ToList();
                    var listing = await inbox.List(new InboxFilter(), ct);

                    var responses = await listing
                        .Where(row => ids.Contains(row.Id))
                        .SelectDto()
                        .ToListAsync(ct);

                    // Back into the order the matcher ranked them in: closest first is the
                    // only order that helps, and a listing sorts by date.
                    return Results.Ok<IReadOnlyList<BankTransactionResponse>>(
                        [.. responses.OrderBy(row => ids.IndexOf(row.Id))]);
                })
                .WithName("GetTransactionBankMatches")
                .Produces<IReadOnlyList<BankTransactionResponse>>()
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

        /// <summary>
        /// What the expense described would be divided into, without recording it.
        /// </summary>
        /// <remarks>
        /// A POST because it takes the whole expense in a body, not because it writes: it
        /// writes nothing. The dialog asks it as the amount, the group and the category
        /// settle down, so the second step shows the division that is actually going to be
        /// stored rather than a client-side re-derivation of it that has to agree to the
        /// cent.
        /// </remarks>
        private RouteHandlerBuilder MapPreviewSplits()
        {
            return group.MapPost("preview", async (
                    CreateTransactionRequest request,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await transactionService.Preview(request, ct));
                })
                .WithName("PreviewTransactionSplits")
                .Produces<SplitPreviewResponse>()
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
                    GroupId = transaction.GroupId,
                    GroupName = transaction.Group != null ? transaction.Group.Name : null,
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
                      // Personal is the absence of a group rather than a group of its own,
                      // so it is asked for that way: true keeps only the rows with no group,
                      // false keeps only the rows with one, and null keeps both.
                      (filter.Personal == null ||
                       (filter.Personal.Value ? transaction.GroupId == null : transaction.GroupId != null)) &&
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
                       (transaction.Group != null && transaction.Group.Name.ToLower().Contains(search)) ||
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

    extension(IQueryable<TransactionSplit> shares)
    {
        /// <summary>
        /// The caller's shares of <paramref name="expenses"/>, one row per expense.
        /// </summary>
        /// <remarks>
        /// A join rather than a walk through <c>split.Transaction</c>, because the join is
        /// also how the narrowing gets in: hand it the filtered expense query and the
        /// shares of everything else fall away, with no second copy of the filter to keep
        /// in step with the first.
        /// </remarks>
        private IQueryable<ExpenseShareResponse> SelectDto(IQueryable<Expense> expenses, Guid userId)
        {
            return from split in shares
                join expense in expenses on split.TransactionId equals expense.Id
                select new ExpenseShareResponse
                {
                    Id = expense.Id,
                    Amount = expense.Amount,
                    // The stored split, not the amount divided by anything. It is the row
                    // every balance is summed from, so a listing that re-derived it could
                    // disagree with the figure the group page shows.
                    Share = split.Amount,
                    DateTime = expense.DateTime,
                    Name = expense.Name,
                    Description = expense.Description,
                    GroupId = expense.GroupId,
                    GroupName = expense.Group != null ? expense.Group.Name : null,
                    PaidByUserId = expense.User.Id,
                    PaidByUserName = expense.User.FirstName +
                                     (expense.User.LastName != null ? " " + expense.User.LastName : ""),
                    PaidByYou = expense.UserId == userId,
                    CategoryId = expense.CategoryId,
                    Category = expense.Category != null ? expense.Category.Name : null
                };
        }

        /// <summary>
        /// The filter, the order and the page, in that order -- the same three the expense
        /// listing applies, and the same <see cref="ApplyFilter"/> applying the first.
        /// </summary>
        internal Task<PagedResponse<ExpenseShareResponse>> ToSharePageAsync(
            IQueryable<Expense> expenses, TransactionFilter filter, SortRequest sort, PageRequest page,
            Guid userId, CancellationToken ct) =>
            shares.SelectDto(expenses.ApplyFilter(filter), userId)
                .ApplySort(sort, ShareSort)
                .ToPageAsync(page, ct);

        /// <summary>The same filter, counted and totalled instead of paged.</summary>
        /// <remarks>
        /// Four figures and four queries. One <c>GROUP BY</c> would answer them together,
        /// and a conditional sum inside it is the kind of thing that translates on one
        /// provider and not the other -- the tests run in memory and production runs on
        /// Npgsql, so a translation failure would first be seen by a person. Three extra
        /// aggregates over an indexed join is not worth taking that risk for.
        /// </remarks>
        internal async Task<ExpenseShareSummaryResponse> ToShareSummaryAsync(
            IQueryable<Expense> expenses, TransactionFilter filter, Guid userId, CancellationToken ct)
        {
            var matches = shares.SelectDto(expenses.ApplyFilter(filter), userId);

            return new ExpenseShareSummaryResponse(
                await matches.CountAsync(ct),
                await matches.SumAsync(row => row.Amount, ct),
                await matches.SumAsync(row => row.Share, ct),
                // What is actually owed. A share of an expense the caller paid for is money
                // they already have -- they are owed the rest of it -- so counting it here
                // would put every personal expense and every dinner they picked up on the
                // wrong side of the same figure the home page reads.
                await matches.Where(row => !row.PaidByYou).SumAsync(row => row.Share, ct));
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
        // Null-safe because a personal expense has no group: the database sorts nulls of
        // its own accord, but the in-memory provider the tests run on would dereference.
        .Key("group", transaction => transaction.Group == null ? null : transaction.Group.Name)
        .Key("paidBy", transaction => transaction.User.FirstName)
        .Default("dateTime")
        .TieBreak(transaction => transaction.Id);

    /// <summary>
    /// The orders the share listing offers: the expense listing's, plus the one figure
    /// only this listing has.
    /// </summary>
    /// <remarks>
    /// Applied to the response rather than to the entity, because the join has already
    /// flattened the group and the payer onto it -- so a key reaches them without a
    /// navigation, and <c>share</c> is a column here rather than a row on another table.
    /// </remarks>
    internal static readonly SortMap<ExpenseShareResponse> ShareSort = new SortMap<ExpenseShareResponse>()
        .Key("dateTime", share => share.DateTime, defaultDescending: true)
        .Key("share", share => share.Share, defaultDescending: true)
        .Key("amount", share => share.Amount, defaultDescending: true)
        .Key("name", share => share.Name)
        .Key("category", share => share.Category)
        .Key("group", share => share.GroupName)
        .Key("paidBy", share => share.PaidByUserName)
        .Default("dateTime")
        .TieBreak(share => share.Id);
}
