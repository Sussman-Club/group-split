using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.ReceiptTranscription;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

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
/// moment it is typed. Once a complete receipt has already been used to produce an
/// itemized ledger, a valid correction updates those dependent shares in the same save so
/// the paper and the ledger cannot drift apart.
/// </para>
/// </remarks>
public static class ReceiptsApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapReceiptDraftsApi()
        {
            var group = routes.MapGroup("/receipts")
                .RequireAuthorization()
                .ProducesStandardProblems()
                .WithTags("Receipts");

            group.MapPost("/transcribe", async (
                    IFormFile file,
                    [FromServices] IReceiptTranscriptionService transcription,
                    CancellationToken ct) =>
                Results.Ok(await transcription.Transcribe(file, ct)))
                .WithName("TranscribeReceiptDraft")
                .Accepts<IFormFile>("multipart/form-data")
                .Produces<ReceiptDraftResponse>()
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status502BadGateway)
                .DisableAntiforgery();

            return group;
        }
    }

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
            group.MapPatchReceiptItem();
            group.MapDeleteReceipt();
            group.MapSetRule();
            group.MapPreviewDivision();
            group.MapDivide();

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

                    return Results.Ok(await receipts.ResponseFor(receipt, id, ct));
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

                    return Results.Ok(await receipts.ResponseFor(receipt, id, ct));
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
        /// Corrects one saved line without requiring the caller to send the whole bill back.
        /// </summary>
        private RouteHandlerBuilder MapPatchReceiptItem()
        {
            return group.MapPatch("/items/{itemId:guid}", async (
                    Guid id,
                    Guid itemId,
                    JsonPatchDocument<ReceiptItemPatch> patch,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    var receipt = await receipts.PatchItem(id, itemId, patch, ct);

                    return Results.Ok(await receipts.ResponseFor(receipt, id, ct));
                })
                .WithName("PatchReceiptItem")
                .Accepts<JsonPatchDocument<ReceiptItemPatch>>("application/json-patch+json")
                .Produces<ReceiptResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        }

        /// <summary>
        /// Replaces who had one line.
        /// </summary>
        /// <remarks>
        /// The call behind tapping your name on a line, and the reason it replaces rather
        /// than adds: un-claiming is then the same operation as claiming, and a client never
        /// has to work out which of the two it is doing.
        /// </remarks>
        private RouteHandlerBuilder MapSetRule()
        {
            return group.MapPut("/items/{itemId:guid}/rule", async (
                    Guid id,
                    Guid itemId,
                    SetReceiptItemRuleRequest request,
                    IReceiptService receipts,
                    CancellationToken ct) =>
                {
                    var receipt = await receipts.SetRule(id, itemId, request, ct);

                    return Results.Ok(await receipts.ResponseFor(receipt, id, ct));
                })
                .WithName("SetReceiptItemRule")
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

    }
}
