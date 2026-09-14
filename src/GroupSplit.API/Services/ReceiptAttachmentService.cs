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
    Task<IReadOnlyList<ReceiptAttachmentResponse>> List(Guid expenseId, CancellationToken ct = default);
    Task<ReceiptAttachmentResponse> Upload(Guid expenseId, IFormFile file, CancellationToken ct = default);
    Task<ReceiptAttachmentDownload> Download(Guid expenseId, Guid attachmentId, CancellationToken ct = default);
    Task Delete(Guid expenseId, Guid attachmentId, CancellationToken ct = default);
}

public sealed record ReceiptAttachmentDownload(byte[] Content, string ContentType, string FileName);

public sealed class ReceiptAttachmentService(
    ICurrentUser currentUser,
    AppDbContext db,
    IAmazonS3 storage,
    IOptions<ReceiptStorageOptions> storageOptions,
    ILogger<ReceiptAttachmentService> logger) : IReceiptAttachmentService
{
    private const long MaximumLength = 10 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf"
    };

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
        ArgumentNullException.ThrowIfNull(file);
        var expense = await MineToChange(expenseId, ct);

        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        if (file.Length is <= 0 or > MaximumLength
            || file.FileName.Length > 256
            || !AllowedTypes.TryGetValue(extension, out var expectedType)
            || !string.Equals(file.ContentType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(ErrorCodes.ReceiptAttachmentInvalid,
                "Choose a JPG, PNG, WebP, or PDF file no larger than 10 MB.");
        }

        var attachment = new ReceiptAttachment
        {
            ExpenseId = expense.Id,
            ReceiptId = await db.Set<Receipt>()
                .Where(receipt => receipt.ExpenseId == expense.Id)
                .Select(receipt => (Guid?)receipt.Id)
                .FirstOrDefaultAsync(ct),
            ObjectKey = $"{expense.Id:N}/{Guid.NewGuid():N}",
            FileName = Path.GetFileName(file.FileName),
            ContentType = expectedType,
            Length = file.Length,
            UploadedByUserId = currentUser.User.Id,
            UploadedAt = DateTimeOffset.UtcNow
        };

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

        return ToResponse(attachment);
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
        await db.SaveChangesAsync(ct);

        // The database is authoritative for which files a caller can address. If storage
        // is temporarily unavailable, keep the expense usable and log the orphan for cleanup.
        try
        {
            await storage.DeleteObjectAsync(storageOptions.Value.BucketName, attachment.ObjectKey, ct);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Could not remove deleted receipt object {ObjectKey}.", attachment.ObjectKey);
        }
    }

    private static System.Linq.Expressions.Expression<Func<ReceiptAttachment, ReceiptAttachmentResponse>> ToResponse() =>
        attachment => new ReceiptAttachmentResponse(attachment.Id, attachment.ExpenseId, attachment.ReceiptId, attachment.FileName,
            attachment.ContentType, attachment.Length, attachment.UploadedAt);

    private static ReceiptAttachmentResponse ToResponse(ReceiptAttachment attachment) =>
        new(attachment.Id, attachment.ExpenseId, attachment.ReceiptId, attachment.FileName,
            attachment.ContentType, attachment.Length, attachment.UploadedAt);

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
        if (expense.GroupId is { } groupId && !await db.Entry(currentUser.User).Collection(user => user.Groups)
                .Query().AnyAsync(group => group.Id == groupId, ct))
        {
            throw new ConflictException(ErrorCodes.TransactionGroupLeft,
                "You are no longer in this expense's group, so its receipt files cannot be changed.");
        }

        return expense;
    }
}
