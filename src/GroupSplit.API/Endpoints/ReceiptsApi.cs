using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Shared;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The itemised bill behind an expense: transcribing it, saying who had what, and dividing
/// by it.
/// </summary>
/// <remarks>
/// Hung off the transaction rather than given a collection of its own, because a receipt has
/// no life apart from what it belongs to -- it is addressed as "this expense's bill" every
/// time, and never by an id somebody holds.
/// <para>
/// Storing a bill and dividing by it are separate calls on purpose. A receipt is transcribed
/// in one go and then claimed line by line by however many people are at dinner; dividing as
/// a side effect of saving would mean refusing every bill that is not fully claimed the
/// moment it is typed.
/// </para>
/// </remarks>
public static class ReceiptsApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapReceiptsApi()
        {
            var group = routes.MapGroup("/transactions/{id:guid}/receipt")
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Receipts");

            group.MapGetReceipt();
            group.MapSaveReceipt();
            group.MapDeleteReceipt();
            group.MapSetClaims();
            group.MapPreviewDivision();
            group.MapDivide();

            var inbox = routes.MapGroup("/inbox/{id:guid}/receipt")
                .RequireAuthorization()
                .ProducesStandardProblems();

            inbox.WithTags("Receipts");

            inbox.MapGetBankRowReceipt();
            inbox.MapSaveBankRowReceipt();
            inbox.MapDeleteBankRowReceipt();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapGetReceipt()
        {
            return group.MapGet(string.Empty, async (
                    Guid id,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    var receipt = await receipts.ForExpense(id, ct);

                    return Results.Ok(await receipts.ResponseFor(receipt, ct));
                })
                .WithName("GetReceipt")
                .Produces<ReceiptResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Transcribes the bill, replacing whatever was there.
        /// </summary>
        /// <remarks>
        /// A PUT because it is the whole receipt every time. A bill is read off a piece of
        /// paper in one sitting and its figures only mean anything together -- a PATCH that
        /// moved the tax without the total would leave a row that does not add up, which is
        /// the one state this refuses to store.
        /// </remarks>
        private RouteHandlerBuilder MapSaveReceipt()
        {
            return group.MapPut(string.Empty, async (
                    Guid id,
                    SaveReceiptRequest request,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    var receipt = await receipts.SaveForExpense(id, request, ct);

                    return Results.Ok(await receipts.ResponseFor(receipt, ct));
                })
                .WithName("SaveReceipt")
                .Produces<ReceiptResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        }

        private RouteHandlerBuilder MapDeleteReceipt()
        {
            return group.MapDelete(string.Empty, async (
                    Guid id,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    await receipts.DeleteForExpense(id, ct);

                    return Results.NoContent();
                })
                .WithName("DeleteReceipt")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Replaces who had one line.
        /// </summary>
        /// <remarks>
        /// The call behind tapping your name on a line, and the reason it replaces rather
        /// than adds: un-claiming is then the same operation as claiming, and a client never
        /// has to work out which of the two it is doing.
        /// </remarks>
        private RouteHandlerBuilder MapSetClaims()
        {
            return group.MapPut("/items/{itemId:guid}/claims", async (
                    Guid id,
                    Guid itemId,
                    SetReceiptItemClaimsRequest request,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    var receipt = await receipts.SetClaims(id, itemId, request, ct);

                    return Results.Ok(await receipts.ResponseFor(receipt, ct));
                })
                .WithName("SetReceiptItemClaims")
                .Produces<ReceiptResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        /// <summary>
        /// What dividing by the bill would come to. Stores nothing.
        /// </summary>
        private RouteHandlerBuilder MapPreviewDivision()
        {
            return group.MapGet("/preview", async (
                    Guid id,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await receipts.Preview(id, ct));
                })
                .WithName("PreviewReceiptDivision")
                .Produces<ReceiptDivisionResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        }

        /// <summary>
        /// Divides the expense by its bill and stores the shares.
        /// </summary>
        private RouteHandlerBuilder MapDivide()
        {
            return group.MapPost("/divide", async (
                    Guid id,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    await receipts.Divide(id, ct);

                    return Results.Ok(await receipts.Preview(id, ct));
                })
                .WithName("DivideByReceipt")
                .Produces<ReceiptDivisionResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        }

        /// <summary>
        /// Takes a bill off an imported row.
        /// </summary>
        /// <remarks>
        /// The way out for a bill nothing will take. Filing a row into a personal expense
        /// leaves its bill where it is -- there is nobody to divide it between -- and the row
        /// is then filed, which both file and link refuse. Without this the bill would sit in
        /// the inbox forever with overwriting it as the only thing anybody could do.
        /// </remarks>
        private RouteHandlerBuilder MapDeleteBankRowReceipt()
        {
            return group.MapDelete(string.Empty, async (
                    Guid id,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    await receipts.DeleteForBankRow(id, ct);

                    return Results.NoContent();
                })
                .WithName("DeleteBankRowReceipt")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapGetBankRowReceipt()
        {
            return group.MapGet(string.Empty, async (
                    Guid id,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    var receipt = await receipts.ForBankRow(id, ct);

                    return Results.Ok(await receipts.ResponseFor(receipt, ct));
                })
                .WithName("GetBankRowReceipt")
                .Produces<ReceiptResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Transcribes a bill against an imported row nobody has filed yet, so a dinner can
        /// be itemised at the table and carried over when the row is filed.
        /// </summary>
        private RouteHandlerBuilder MapSaveBankRowReceipt()
        {
            return group.MapPut(string.Empty, async (
                    Guid id,
                    SaveReceiptRequest request,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    var receipt = await receipts.SaveForBankRow(id, request, ct);

                    return Results.Ok(await receipts.ResponseFor(receipt, ct));
                })
                .WithName("SaveBankRowReceipt")
                .Produces<ReceiptResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        }
    }
}
