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
            group.MapGetMonthlyExposure();
            group.MapGetById();
            group.MapBankMatches();
            group.MapCreate();
            group.MapPreviewSplits();
            group.MapPreviewUpdatedSplits();
            group.MapUpdate();
            group.MapSetDivisionSource();
            group.MapReattach();
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
                    bool? owedOnly,
                    ICurrentUser currentUser,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var shares = await transactionService.Shares(ct);
                    var expenses = await transactionService.List(ct);

                    return Results.Ok(await shares
                        .ToSharePageAsync(expenses, filter, sort, page, currentUser.User.Id,
                            owedOnly ?? false, ct));
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
                    bool? owedOnly,
                    ICurrentUser currentUser,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var shares = await transactionService.Shares(ct);
                    var expenses = await transactionService.List(ct);

                    return Results.Ok(await shares
                        .ToShareSummaryAsync(expenses, filter, currentUser.User.Id,
                            owedOnly ?? false, ct));
                })
                .WithName("GetTransactionSharesSummary")
                .Produces<ExpenseShareSummaryResponse>();
        }

        /// <summary>
        /// What the caller paid and what it cost them, month by month, over the same filter
        /// the listings take.
        /// </summary>
        /// <remarks>
        /// The one series in the product worth drawing, and the one place a chart is
        /// unarguable: a migrated workbook of forty-odd months is not readable as a table.
        /// Two figures rather than one, because the gap between them is the story -- it is
        /// how much somebody is habitually fronting and waiting to get back.
        /// <para>
        /// Two grouped reads rather than one join. What was paid and what was owed live in
        /// different tables and a month can have one without the other -- a month where
        /// somebody paid for nothing but owed a share of a flatmate's rent is a real month
        /// and an inner join would drop it.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapGetMonthlyExposure()
        {
            return group.MapGet("monthly", async (
                    [AsParameters] TransactionFilter filter,
                    ICurrentUser currentUser,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var user = currentUser.User;
                    var expenses = (await transactionService.List(ct)).ApplyFilter(filter);
                    var shares = await transactionService.Shares(ct);

                    var paid = await expenses
                        .Where(expense => expense.UserId == user.Id)
                        .GroupBy(expense => new { expense.DateTime.Year, expense.DateTime.Month })
                        .Select(month => new
                        {
                            month.Key.Year,
                            month.Key.Month,
                            Total = month.Sum(expense => expense.Amount)
                        })
                        .ToListAsync(ct);

                    var owed = await (from split in shares
                                      join expense in expenses on split.TransactionId equals expense.Id
                                      group split.Amount by new { expense.DateTime.Year, expense.DateTime.Month }
                                      into month
                                      select new
                                      {
                                          month.Key.Year,
                                          month.Key.Month,
                                          Total = month.Sum()
                                      })
                        .ToListAsync(ct);

                    var months = paid.Select(row => (row.Year, row.Month))
                        .Union(owed.Select(row => (row.Year, row.Month)))
                        .OrderBy(month => month)
                        .Select(month => new MonthlyExposureResponse(
                            new DateOnly(month.Year, month.Month, 1),
                            paid.FirstOrDefault(row => row.Year == month.Year && row.Month == month.Month)?.Total ?? 0m,
                            owed.FirstOrDefault(row => row.Year == month.Year && row.Month == month.Month)?.Total ?? 0m))
                        .ToList();

                    return Results.Ok(months);
                })
                .WithName("GetMonthlyExposure")
                .Produces<MonthlyExposureResponse[]>();
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

        /// <summary>
        /// The same preview, for an expense that already exists.
        /// </summary>
        /// <remarks>
        /// A POST with the whole edited expense in the body rather than the patch the save
        /// sends, because a preview has nothing to patch against -- the dialog holds the
        /// edited model already and a patch would have to be applied to something first
        /// just to be read back.
        /// <para>
        /// Its own route and not <c>preview</c> with an id, because the two answer
        /// different questions: this one divides by the version the expense was written
        /// under, and that is what the edit dialog has to show or its numbers disagree with
        /// the save it is previewing.
        /// </para>
        /// <para>
        /// <c>?redivide=true</c> asks the other question the dialog can put: what dividing
        /// it again by its rule would come to. A query parameter rather than a field on the
        /// request, because the request is the save contract and "no shares" means keep them
        /// there -- a second way to say otherwise inside it is how a patch nobody read
        /// carefully re-divides 733 expenses. Absent it, this answers as <c>PATCH</c> treats
        /// silence.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapPreviewUpdatedSplits()
        {
            return group.MapPost("{id:guid}/preview", async (
                    Guid id,
                    UpdateTransactionRequest request,
                    ITransactionService transactionService,
                    CancellationToken ct,
                    bool redivide = false) =>
                {
                    return Results.Ok(await transactionService.PreviewUpdate(id, request, redivide, ct));
                })
                .WithName("PreviewUpdatedTransactionSplits")
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

                    if (patchDocument.Touches("/groupId") && !patchDocument.Touches("/splits"))
                    {
                        transactionUpdateRequest.Splits = null;
                    }

                    patchDocument.ApplyTo(transactionUpdateRequest);

                    if (!PatchedModel.IsValid(transactionUpdateRequest, out var invalid))
                        return invalid;

                    await transactionService.Update(id, transactionUpdateRequest, ct);

                    var details = await transactionService.GetDetails(id, ct);

                    return Results.Ok(details);
                })
                .WithName("UpdateTransaction")
                .Produces<TransactionDetailsResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        }

        /// <summary>
        /// Records what divided one expense, or -- with null -- that its shares are its own.
        /// </summary>
        /// <remarks>
        /// Provenance, and not a division. Not one share moves: the amounts stay exactly as
        /// they are and only the account of what produced them changes. It is its own route
        /// rather than a field on the patch for that reason -- the patch is where a division
        /// is decided, and a field there that looked like provenance and quietly re-divided
        /// is the shape of the 2026-09-08 incident.
        /// <para>
        /// What it does change is what a later edit does. An expense recorded as a rule's is
        /// worked out again when its amount or its payer moves; one whose shares are its own
        /// is left alone.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapSetDivisionSource()
        {
            return group.MapPut("{id:guid}/division-source", async (
                    Guid id,
                    SetDivisionSourceRequest request,
                    IExpenseProvenance provenance,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    await provenance.SetDivisionSource(id, request, ct);

                    return Results.Ok(await transactionService.GetDetails(id, ct));
                })
                .WithName("SetTransactionDivisionSource")
                .Produces<TransactionDetailsResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        /// <summary>
        /// Points a group's expenses at the version of their rule that was in force on the
        /// day each was spent.
        /// </summary>
        /// <remarks>
        /// For a back catalogue whose provenance was guessed. A migration out of a workbook
        /// pointed every categorised expense at its category's only version, because at the
        /// time that was the only one; once the rule's real history is written, the dates on
        /// the rows are enough to sort out which version each expense actually fell under.
        /// <para>
        /// It divides nothing. The splitter is not on this path at all -- the one column it
        /// writes is the version pointer -- so the group's balances are the same afterwards
        /// to the cent. <c>dryRun</c> answers the same summary and saves none of it.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapReattach()
        {
            return group.MapPost("reattach", async (
                    ReattachTransactionsRequest request,
                    IExpenseProvenance provenance,
                    CancellationToken ct) => Results.Ok(await provenance.Reattach(request, ct)))
                .WithName("ReattachTransactions")
                .Produces<ReattachSummaryResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Removes an expense, or a settlement between two members.
        /// </summary>
        /// <remarks>
        /// The only endpoint under /transactions that a transfer's id gets through. The
        /// others read the expenses and answer 404 for one, which is right for a listing
        /// and was wrong here: the Activity tab shows settlements precisely because a
        /// balance that moved needs explaining, and the row explaining it could not be
        /// taken back when it was wrong.
        /// </remarks>
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
                    Category = transaction.Category != null ? transaction.Category.Name : null,
                    MerchantId = transaction.MerchantId,
                    MerchantName = transaction.Merchant != null ? transaction.Merchant.Name : null,
                    MerchantLogoUrl = transaction.Merchant != null ? transaction.Merchant.LogoUrl : null
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

    extension(IQueryable<ExpenseShareResponse> rows)
    {
        /// <summary>
        /// Keeps only the rows that are actually a debt: a share of something somebody else
        /// paid for.
        /// </summary>
        /// <remarks>
        /// The difference between two of the three views the page offers. <em>Everything you
        /// are in</em> is every share; <em>your share</em> is what other people's expenses
        /// cost you, and a share of an expense you paid for yourself is not that -- you are
        /// owed the rest of it.
        /// </remarks>
        internal IQueryable<ExpenseShareResponse> OwedOnly(bool owedOnly) =>
            owedOnly ? rows.Where(row => !row.PaidByYou) : rows;
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
                    Category = expense.Category != null ? expense.Category.Name : null,
                    MerchantId = expense.MerchantId,
                    MerchantName = expense.Merchant != null ? expense.Merchant.Name : null,
                    MerchantLogoUrl = expense.Merchant != null ? expense.Merchant.LogoUrl : null
                };
        }

        /// <summary>
        /// The filter, the order and the page, in that order -- the same three the expense
        /// listing applies, and the same <see cref="ApplyFilter"/> applying the first.
        /// </summary>
        internal Task<PagedResponse<ExpenseShareResponse>> ToSharePageAsync(
            IQueryable<Expense> expenses, TransactionFilter filter, SortRequest sort, PageRequest page,
            Guid userId, bool owedOnly, CancellationToken ct) =>
            shares.SelectDto(expenses.ApplyFilter(filter), userId)
                .OwedOnly(owedOnly)
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
            IQueryable<Expense> expenses, TransactionFilter filter, Guid userId, bool owedOnly,
            CancellationToken ct)
        {
            var matches = shares.SelectDto(expenses.ApplyFilter(filter), userId).OwedOnly(owedOnly);

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
