using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The rows a sync brought in, and what a person does with them: add one to a group, keep
/// it personal, ignore it, put it back.
/// </summary>
public static class InboxApi
{
    /// <summary>
    /// Newest first, because an inbox is read from the top and the top is what just arrived.
    /// </summary>
    internal static readonly SortMap<BankTransaction> Sort = new SortMap<BankTransaction>()
        .Key("date", row => row.Date, defaultDescending: true)
        .Key("amount", row => row.Amount, defaultDescending: true)
        .Key("merchant", row => row.MerchantName)
        .Default("date")
        .TieBreak(row => row.Id);

    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapInboxApi()
        {
            var group = routes.MapGroup("/inbox")
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Inbox");

            group.MapList();
            group.MapSummary();
            group.MapMatches();
            group.MapFile();
            group.MapLink();
            group.MapDismissMatch();
            group.MapIgnore();
            group.MapRestore();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapList()
        {
            return group.MapGet(string.Empty, async (
                    [AsParameters] InboxFilter filter,
                    [AsParameters] SortRequest sort,
                    [AsParameters] PageRequest page,
                    IInboxService inbox,
                    CancellationToken ct) =>
                {
                    var rows = await inbox.List(filter, ct);

                    var found = await rows
                        .ApplySort(sort, Sort)
                        .SelectDto()
                        .ToPageAsync(page, ct);

                    return Results.Ok(await found.WithDuplicatesAsync(inbox, ct));
                })
                .WithName("GetInbox")
                .Produces<PagedResponse<BankTransactionResponse>>()
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }

        /// <summary>
        /// Its own route rather than a count on the listing: the badge is on every page and
        /// asking for it should not also fetch a page of rows.
        /// </summary>
        private RouteHandlerBuilder MapSummary()
        {
            return group.MapGet("summary", async (
                    bool? withDuplicates,
                    IInboxService inbox,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await inbox.Summary(withDuplicates ?? false, ct));
                })
                .WithName("GetInboxSummary")
                .Produces<InboxSummaryResponse>();
        }

        /// <summary>
        /// What this row could already be, asked on its own.
        /// </summary>
        /// <remarks>
        /// The listing carries the same answer for every row it shows, so this is for the
        /// one row a client is holding on its own: a dialog opened from a link, or a second
        /// look after something changed elsewhere.
        /// </remarks>
        private RouteHandlerBuilder MapMatches()
        {
            return group.MapGet("{id:guid}/matches", async (
                    Guid id,
                    IInboxService inbox,
                    CancellationToken ct) =>
                {
                    var matches = await inbox.Matches(id, ct);

                    return Results.Ok(matches.ToResponses());
                })
                .WithName("GetBankTransactionMatches")
                .Produces<IReadOnlyList<ExpenseMatchResponse>>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Attaches the row to an expense that is already recorded, rather than making a
        /// second one for the same money.
        /// </summary>
        /// <remarks>
        /// One of the two answers to the refusal filing gives when the two look alike; the
        /// other is filing anyway. What comes back is the expense that was already there,
        /// now carrying the bank's row -- the same link filing would have made for a new
        /// one, and no change to anything else about it.
        /// </remarks>
        private RouteHandlerBuilder MapLink()
        {
            return group.MapPost("{id:guid}/link", async (
                    Guid id,
                    LinkBankTransactionRequest request,
                    IInboxService inbox,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var expense = await inbox.Link(id, request, ct);

                    var linked = await transactionService.Get(expense.Id, ct);
                    var response = await linked.SelectDto().FirstOrDefaultAsync(ct);

                    return Results.Ok(response);
                })
                .WithName("LinkBankTransaction")
                .Produces<TransactionResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        }

        /// <summary>
        /// Says the two are not the same money after all, so the pair is never suggested
        /// again -- a suggestion nobody can get rid of being worse than none.
        /// </summary>
        private RouteHandlerBuilder MapDismissMatch()
        {
            return group.MapPost("{id:guid}/dismiss-match", async (
                    Guid id,
                    DismissBankMatchRequest request,
                    IInboxService inbox,
                    CancellationToken ct) =>
                {
                    await inbox.DismissMatch(id, request, ct);
                    return Results.NoContent();
                })
                .WithName("DismissBankTransactionMatch")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapFile()
        {
            return group.MapPost("{id:guid}/file", async (
                    Guid id,
                    FileBankTransactionRequest request,
                    IInboxService inbox,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var expense = await inbox.File(id, request, ct);

                    // Read back through the transaction service, the way creating one
                    // directly does, so an expense that arrived from a bank is described by
                    // exactly the same projection as one somebody typed.
                    var created = await transactionService.Get(expense.Id, ct);
                    var response = await created.SelectDto().FirstOrDefaultAsync(ct);

                    return Results.Created($"/transactions/{expense.Id}", response);
                })
                .WithName("FileBankTransaction")
                .Produces<TransactionResponse>(StatusCodes.Status201Created)
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        }

        private RouteHandlerBuilder MapIgnore()
        {
            return group.MapPost("{id:guid}/ignore", async (
                    Guid id,
                    IInboxService inbox,
                    CancellationToken ct) =>
                {
                    await inbox.Ignore(id, ct);
                    return Results.NoContent();
                })
                .WithName("IgnoreBankTransaction")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapRestore()
        {
            return group.MapPost("{id:guid}/restore", async (
                    Guid id,
                    IInboxService inbox,
                    CancellationToken ct) =>
                {
                    await inbox.Restore(id, ct);
                    return Results.NoContent();
                })
                .WithName("RestoreBankTransaction")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }

    extension(IQueryable<BankTransaction> rows)
    {
        /// <summary>
        /// The wire shape, projected in the database so a page reads the columns it shows
        /// and no more. The account and institution come along because a row means little
        /// without saying which card it was on.
        /// </summary>
        internal IQueryable<BankTransactionResponse> SelectDto() =>
            rows.Select(row => new BankTransactionResponse(
                row.Id,
                row.Date,
                row.Amount,
                row.Currency,
                row.Description,
                row.MerchantName,
                row.ProviderCategory,
                row.ProviderCategoryDetailed,
                row.AuthorizedDate,
                row.PaymentChannel,
                row.City,
                row.LogoUrl,
                row.Pending,
                row.Status == BankTransactionStatus.Filed
                    ? InboxStatus.Filed
                    : row.Status == BankTransactionStatus.Ignored
                        ? InboxStatus.Ignored
                        : InboxStatus.New,
                row.FiledAs == null ? null : row.FiledAs.Id,
                row.RemovedAt,
                row.Account.Name,
                row.Account.Connection.InstitutionName));
    }

    extension(PagedResponse<BankTransactionResponse> page)
    {
        /// <summary>
        /// The same page, with each waiting row carrying the expenses it could already be.
        /// </summary>
        /// <remarks>
        /// After the paging and in one pass, rather than a query per row: the inbox is
        /// where a person decides what a row is, so the warning has to be on the row they
        /// are looking at, and twenty-five round trips to say so is a page nobody waits for.
        /// </remarks>
        internal async Task<PagedResponse<BankTransactionResponse>> WithDuplicatesAsync(
            IInboxService inbox, CancellationToken ct)
        {
            var waiting = page.Items
                .Where(row => row.Status == InboxStatus.New && !row.IsCredit)
                .Select(row => row.Id)
                .ToList();

            var matches = await inbox.Matches(waiting, ct);

            if (matches.Count == 0)
                return page;

            var items = page.Items
                .Select(row => matches.TryGetValue(row.Id, out var found)
                    ? row with { PossibleDuplicates = found.ToResponses() }
                    : row)
                .ToList();

            return new PagedResponse<BankTransactionResponse>(items, page.Page, page.PageSize, page.TotalCount);
        }
    }
}
