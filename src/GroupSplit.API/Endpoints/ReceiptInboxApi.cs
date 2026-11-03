using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Mvc;

namespace GroupSplit.API.Endpoints;

public static class ReceiptInboxApi
{
    public static void MapReceiptInbox(this RouteGroupBuilder receipts)
    {
        var group = receipts.MapGroup(string.Empty).ProducesStandardProblems()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/inbox/{receiptId:guid}/files/{attachmentId:guid}/transcribe", async (Guid receiptId, Guid attachmentId,
            IReceiptAttachmentService attachments, GroupSplit.API.Services.ReceiptTranscription.IReceiptTranscriptionService transcription, CancellationToken ct) =>
        {
            var source = await attachments.DownloadDraft(receiptId, attachmentId, ct);
            using var stream = new MemoryStream(source.Content);
            var form = new FormFile(stream, 0, source.Content.Length, "file", source.FileName) { Headers = new HeaderDictionary(), ContentType = source.ContentType };
            return Results.Ok(await transcription.Transcribe(form, ct));
        }).WithName("TranscribeInboxReceipt").Produces<ReceiptDraftResponse>()
          .ProducesProblem(StatusCodes.Status502BadGateway)
          .RateLimitedAs(ApiRateLimitPolicies.Transcription);
        group.MapGet("/inbox", async (ReceiptInboxService service, CancellationToken ct) => Results.Ok(await service.List(ct)))
            .WithName("ListReceiptInbox").Produces<IReadOnlyList<ReceiptInboxResponse>>();
        group.MapGet("/inbox/{receiptId:guid}", async (Guid receiptId, ReceiptInboxService service, CancellationToken ct) => Results.Ok(await service.Get(receiptId, ct)))
            .WithName("GetReceiptInbox").Produces<ReceiptInboxResponse>();
        group.MapPut("/inbox/{receiptId:guid}", async (Guid receiptId, SaveReceiptInboxRequest request, ReceiptInboxService service, CancellationToken ct) => Results.Ok(await service.Save(receiptId, request, ct)))
            .WithName("SaveReceiptInbox").Produces<ReceiptInboxResponse>();
        group.MapDelete("/inbox/{receiptId:guid}", async (Guid receiptId, ReceiptInboxService service, CancellationToken ct) => { await service.Delete(receiptId, ct); return Results.NoContent(); })
            .WithName("DeleteReceiptInbox").Produces(StatusCodes.Status204NoContent);
        group.MapGet("/inbox/{receiptId:guid}/matches", async (Guid receiptId, ReceiptInboxService service, CancellationToken ct) => Results.Ok(await service.Matches(receiptId, ct)))
            .WithName("MatchReceiptInbox").Produces<IReadOnlyList<ReceiptExpenseCandidate>>();
        group.MapPost("/inbox/{receiptId:guid}/file", async (Guid receiptId, FileReceiptInboxRequest request, ReceiptInboxService service, CancellationToken ct) => Results.Ok(await service.File(receiptId, request, ct)))
            .WithName("FileReceiptInbox").Produces<ReceiptInboxResponse>();
        group.MapPost("/inbox/{receiptId:guid}/files", async (Guid receiptId, IFormFile file, IReceiptAttachmentService service, CancellationToken ct) => Results.Ok(await service.UploadDraft(receiptId, file, ct)))
            .WithName("UploadInboxReceiptFile").Produces<ReceiptAttachmentResponse>().Accepts<IFormFile>("multipart/form-data").DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ReceiptAttachmentsApi.MaximumRequestLength));
        group.MapGet("/inbox/{receiptId:guid}/files/{attachmentId:guid}", async (Guid receiptId, Guid attachmentId, IReceiptAttachmentService service, CancellationToken ct) =>
        {
            var file = await service.DownloadDraft(receiptId, attachmentId, ct);
            return Results.File(file.Content, file.ContentType);
        }).WithName("DownloadInboxReceiptFile").Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");
    }
}
