using GroupSplit.API.Extensions;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Mvc;

namespace GroupSplit.API.Endpoints;

/// <summary>Private source images and documents attached to expenses.</summary>
public static class ReceiptAttachmentsApi
{
    private const long MaximumRequestLength = 11 * 1024 * 1024;

    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapReceiptAttachmentsApi()
        {
            var group = routes.MapGroup("/transactions/{id:guid}/receipt-attachments")
                .RequireAuthorization()
                .ProducesStandardProblems()
                .WithTags("Receipts");

            group.MapList();
            group.MapUpload();
            group.MapDownload();
            group.MapDelete();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapList()
        {
            return group.MapGet(string.Empty, async (
                    Guid id,
                    IReceiptAttachmentService attachments,
                    CancellationToken ct) =>
                    Results.Ok(await attachments.List(id, ct)))
                .WithName("GetReceiptAttachments")
                .Produces<IReadOnlyList<ReceiptAttachmentResponse>>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapUpload()
        {
            return group.MapPost(string.Empty, async (
                    Guid id,
                    IFormFile file,
                    IReceiptAttachmentService attachments,
                    CancellationToken ct) =>
                    Results.Ok(await attachments.Upload(id, file, ct)))
                .WithName("UploadReceiptAttachment")
                .Accepts<IFormFile>("multipart/form-data")
                .Produces<ReceiptAttachmentResponse>()
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .DisableAntiforgery()
                .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestLength));
        }

        private RouteHandlerBuilder MapDownload()
        {
            return group.MapGet("{attachmentId:guid}", async (
                    Guid id,
                    Guid attachmentId,
                    IReceiptAttachmentService attachments,
                    CancellationToken ct) =>
                {
                    var file = await attachments.Download(id, attachmentId, ct);
                    return Results.File(file.Content, file.ContentType, file.FileName);
                })
                .WithName("DownloadReceiptAttachment")
                .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapDelete()
        {
            return group.MapDelete("{attachmentId:guid}", async (
                    Guid id,
                    Guid attachmentId,
                    IReceiptAttachmentService attachments,
                    CancellationToken ct) =>
                {
                    await attachments.Delete(id, attachmentId, ct);
                    return Results.NoContent();
                })
                .WithName("DeleteReceiptAttachment")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }
}
