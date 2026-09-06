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
    private static readonly SortMap<BankTransaction> Sorting = new SortMap<BankTransaction>()
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
            group.MapFile();
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

                    return Results.Ok(await rows
                        .ApplySort(sort, Sorting)
                        .SelectDto()
                        .ToPageAsync(page, ct));
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
                    IInboxService inbox,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await inbox.Summary(ct));
                })
                .WithName("GetInboxSummary")
                .Produces<InboxSummaryResponse>();
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
}
