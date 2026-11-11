using Amazon.S3;
using Amazon.S3.Model;
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services;

public interface IReceiptAttachmentService
{
    Task<ReceiptAttachmentResponse> UploadDraft(Guid receiptId, IFormFile file, CancellationToken ct = default);
    Task<ReceiptAttachmentDownload> DownloadDraft(Guid receiptId, Guid attachmentId, CancellationToken ct = default);
    Task<IReadOnlyList<ReceiptAttachmentResponse>> List(Guid expenseId, CancellationToken ct = default);
    Task<ReceiptAttachmentResponse> Upload(Guid expenseId, IFormFile file, CancellationToken ct = default);
    Task<ReceiptAttachmentDownload> Download(Guid expenseId, Guid attachmentId, CancellationToken ct = default);
    Task Delete(Guid expenseId, Guid attachmentId, CancellationToken ct = default);

    /// <summary>
    /// Stages attachment changes with the expense deletion, returning object keys for
    /// cleanup after the database changes have been saved.
    /// </summary>
    /// <param name="returnReceiptId">The private inbox receipt to preserve, if any.</param>
    Task<IReadOnlyList<string>> PrepareDeleteForExpense(Guid expenseId, Guid? returnReceiptId, CancellationToken ct = default);
}

public sealed record ReceiptAttachmentDownload(byte[] Content, string ContentType, string FileName);

public sealed class ReceiptAttachmentService(
    ICurrentUser currentUser,
    AppDbContext db,
    IAmazonS3 storage,
    IReceiptFiles files,
    IAccountDataGuard accountData,
    IOptions<ReceiptStorageOptions> storageOptions,
    ILogger<ReceiptAttachmentService> logger,
    TimeProvider clock) : IReceiptAttachmentService
{
    private const long MaximumLength = 10 * 1024 * 1024;

    public async Task<IReadOnlyList<ReceiptAttachmentResponse>> List(Guid expenseId, CancellationToken ct = default)
    {
        await VisibleExpense(expenseId, ct);
        return await db.Set<ReceiptAttachment>()
            .Where(attachment => attachment.ExpenseId == expenseId)
            .OrderByDescending(attachment => attachment.UploadedAt)
            .Select(ToResponse())
            .ToListAsync(ct);
    }

    public async Task<ReceiptAttachmentResponse> Upload(Guid expenseId, IFormFile file, CancellationToken ct = default)
    {
        await using var lease = await accountData.Acquire(currentUser.User.Id, ct: ct);
        ArgumentNullException.ThrowIfNull(file);
        var expense = await MineToChange(expenseId, ct);

        var attachment = NewAttachment(file, expense.Id, expense.Id);
        attachment.ReceiptId = await db.Set<Receipt>()
            .Where(receipt => receipt.ExpenseId == expense.Id)
            .Select(receipt => (Guid?)receipt.Id)
            .FirstOrDefaultAsync(ct);

        await Store(attachment, file, ct);
        return ToResponse(attachment);
    }

    public async Task<ReceiptAttachmentResponse> UploadDraft(Guid receiptId, IFormFile file, CancellationToken ct = default)
    {
        await using var lease = await accountData.Acquire(currentUser.User.Id, ct: ct);
        var contentType = Validate(file);
        await using var hashStream = file.OpenReadStream();
        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(hashStream, ct));
        var key = $"private/{currentUser.User.Id:N}/{hash}";
        var existing = await db.Set<ReceiptAttachment>().Include(a => a.Receipt)
            .FirstOrDefaultAsync(a => a.ObjectKey == key, ct);
        if (existing is not null)
        {
            // A retry opens the existing private draft rather than creating another one.
            if (existing.Receipt is { ExpenseId: null } draft && draft.OwnerUserId == currentUser.User.Id)
            {
                // The content hash identifies the same bytes. Restore the object as well as
                // returning its existing metadata if storage was cleared independently.
                await using var retryStream = file.OpenReadStream();
                await storage.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = storageOptions.Value.BucketName,
                    Key = existing.ObjectKey,
                    InputStream = retryStream,
                    ContentType = existing.ContentType
                }, ct);
                return ToResponse(existing);
            }
            throw new ConflictException(ErrorCodes.ReceiptSourceAlreadyImported,
                "This source file is already attached to an expense. Review the existing expense instead.")
                .WithExtension("expenseId", existing.ExpenseId);
        }
        var receipt = await db.Set<Receipt>().FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null)
            db.Add(new Receipt { Id = receiptId, OwnerUserId = currentUser.User.Id, Subtotal = 0, Total = 0 });
        else if (receipt.OwnerUserId != currentUser.User.Id || receipt.ExpenseId != null)
            throw new NotFoundException(ErrorCodes.ReceiptNotFound, "Receipt not found.");
        // The new draft and attachment are saved together only after storage succeeds.
        var attachment = NewAttachment(file, null, receiptId);
        attachment.ObjectKey = key;
        attachment.ReceiptId = receiptId;
        await Store(attachment, file, ct);
        return ToResponse(attachment);
    }

    public async Task<ReceiptAttachmentDownload> DownloadDraft(Guid receiptId, Guid attachmentId, CancellationToken ct = default)
    {
        await FindDraft(receiptId, ct);
        var attachment = await db.Set<ReceiptAttachment>().FirstOrDefaultAsync(a => a.Id == attachmentId && a.ReceiptId == receiptId && a.ExpenseId == null, ct)
            ?? throw new NotFoundException(ErrorCodes.ReceiptAttachmentNotFound, "Receipt file not found.");
        GetObjectResponse response;
        try { response = await storage.GetObjectAsync(storageOptions.Value.BucketName, attachment.ObjectKey, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { throw new NotFoundException(ErrorCodes.ReceiptAttachmentNotFound, "Receipt file is missing. Upload the original again."); }
        using (response)
        {
            await using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return new(buffer.ToArray(), attachment.ContentType, attachment.FileName);
        }
    }

    private async Task FindDraft(Guid id, CancellationToken ct)
    {
        if (!await db.Set<Receipt>().AnyAsync(r => r.Id == id && r.OwnerUserId == currentUser.User.Id && r.ExpenseId == null, ct))
            throw new NotFoundException(ErrorCodes.ReceiptNotFound, "Receipt not found.");
    }
    private ReceiptAttachment NewAttachment(IFormFile file, Guid? expenseId, Guid ownerId)
    {
        var contentType = Validate(file);
        return new ReceiptAttachment
        {
            ExpenseId = expenseId,
            ObjectKey = $"{ownerId:N}/{Guid.NewGuid():N}",
            FileName = Path.GetFileName(file.FileName),
            ContentType = contentType,
            Length = file.Length,
            UploadedByUserId = currentUser.User.Id,
            UploadedAt = clock.GetUtcNow()
        };
    }

    private async Task Store(ReceiptAttachment attachment, IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        await storage.PutObjectAsync(new PutObjectRequest
        {
            BucketName = storageOptions.Value.BucketName,
            Key = attachment.ObjectKey,
            InputStream = stream,
            ContentType = attachment.ContentType
        }, ct);

        try
        {
            db.Add(attachment);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // The object and row cannot share a transaction. If the row write fails, remove
            // the object so the failed upload does not leave an unaddressable file behind.
            try
            {
                await storage.DeleteObjectAsync(storageOptions.Value.BucketName, attachment.ObjectKey, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                logger.LogError(cleanupError, "Could not remove receipt object {ObjectKey} after its metadata write failed.", attachment.ObjectKey);
            }

            throw;
        }
    }

    public async Task<ReceiptAttachmentDownload> Download(Guid expenseId, Guid attachmentId, CancellationToken ct = default)
    {
        await VisibleExpense(expenseId, ct);
        var attachment = await FindAttachment(expenseId, attachmentId, ct);
        using var response = await storage.GetObjectAsync(storageOptions.Value.BucketName, attachment.ObjectKey, ct);
        await using var buffer = new MemoryStream(checked((int)attachment.Length));
        await response.ResponseStream.CopyToAsync(buffer, ct);
        return new ReceiptAttachmentDownload(buffer.ToArray(), attachment.ContentType, attachment.FileName);
    }

    public async Task Delete(Guid expenseId, Guid attachmentId, CancellationToken ct = default)
    {
        await MineToChange(expenseId, ct);
        var attachment = await FindAttachment(expenseId, attachmentId, ct);
        db.Remove(attachment);
        files.Enqueue([attachment.ObjectKey]);
        await db.SaveChangesAsync(ct);

        // The database is authoritative for which files a caller can address. If storage
        // is temporarily unavailable, keep the expense usable and log the orphan for cleanup.
        await files.DeleteAsync([attachment.ObjectKey], "Could not remove deleted receipt object.", ct);
    }

    public async Task<IReadOnlyList<string>> PrepareDeleteForExpense(Guid expenseId, Guid? returnReceiptId, CancellationToken ct = default)
    {
        await MineToChange(expenseId, ct);
        var attachments = await db.Set<ReceiptAttachment>()
            .Where(attachment => attachment.ExpenseId == expenseId)
            .ToListAsync(ct);

        if (attachments.Count == 0)
            return [];

        var returning = attachments
            .Where(attachment => returnReceiptId is not null && attachment.ReceiptId == returnReceiptId)
            .ToList();

        foreach (var attachment in returning)
        {
            attachment.ExpenseId = null;
        }

        var deleted = attachments.Except(returning).ToList();

        db.RemoveRange(deleted);
        files.Enqueue(deleted.Select(attachment => attachment.ObjectKey));
        return deleted.Select(attachment => attachment.ObjectKey).ToArray();
    }

    private static System.Linq.Expressions.Expression<Func<ReceiptAttachment, ReceiptAttachmentResponse>> ToResponse() =>
        attachment => new ReceiptAttachmentResponse(attachment.Id, attachment.ExpenseId, null, attachment.ReceiptId, attachment.FileName,
            attachment.ContentType, attachment.Length, attachment.UploadedAt);

    private static ReceiptAttachmentResponse ToResponse(ReceiptAttachment attachment) =>
        new(attachment.Id, attachment.ExpenseId, null, attachment.ReceiptId, attachment.FileName,
            attachment.ContentType, attachment.Length, attachment.UploadedAt);

    private static string Validate(IFormFile file)
    {
        var contentType = ReceiptFileTypes.Resolve(file.FileName, file.ContentType);
        if (file.Length is <= 0 or > MaximumLength
            || file.FileName.Length > 256
            || contentType is null)
        {
            throw new ValidationException(ErrorCodes.ReceiptAttachmentInvalid,
                "Choose a JPG, PNG, WebP, or PDF file no larger than 10 MB.");
        }
        return contentType;
    }

    private async Task<ReceiptAttachment> FindAttachment(Guid expenseId, Guid attachmentId, CancellationToken ct) =>
        await db.Set<ReceiptAttachment>().FirstOrDefaultAsync(
            attachment => attachment.Id == attachmentId && attachment.ExpenseId == expenseId, ct)
        ?? throw new NotFoundException(ErrorCodes.ReceiptAttachmentNotFound, "Receipt file not found.");

    private async Task<Expense> VisibleExpense(Guid expenseId, CancellationToken ct)
    {
        var user = currentUser.User;
        var groups = db.Entry(user).Collection(candidate => candidate.Groups).Query();
        return await db.Set<Expense>().FirstOrDefaultAsync(expense => expense.Id == expenseId &&
                   (groups.Any(group => group.Id == expense.GroupId) || expense.UserId == user.Id), ct)
               ?? throw new NotFoundException(ErrorCodes.TransactionNotFound, "Expense not found.");
    }

    private async Task<Expense> MineToChange(Guid expenseId, CancellationToken ct)
    {
        var expense = await VisibleExpense(expenseId, ct);
        if (!await db.Entry(currentUser.User).Collection(user => user.Groups)
                .Query().AnyAsync(group => group.Id == expense.GroupId, ct))
        {
            throw new ConflictException(ErrorCodes.TransactionGroupLeft,
                "You are no longer in this expense's group, so its receipt files cannot be changed.");
        }

        return expense;
    }


}
