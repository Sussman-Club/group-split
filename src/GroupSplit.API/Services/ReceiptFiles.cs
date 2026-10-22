using Amazon.S3;
using GroupSplit.API.Extensions;
using Microsoft.Extensions.Options;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Deletes stored receipt files, for every path that drops the rows naming them.
/// </summary>
/// <remarks>
/// A receipt file is two things that cannot share a transaction: a
/// <see cref="Data.Entities.ReceiptAttachment"/> row and an object in the bucket. Every
/// caller deletes the row first and saves, then hands the keys here -- so a caller can never
/// address a file that is gone, and the worst a storage outage leaves is an object nothing
/// points at, which is logged by key for cleanup.
/// <para>
/// Its own service rather than a method on <see cref="IReceiptAttachmentService"/>, because
/// two of the paths that need it run with nobody signed in: a sync withdrawing a row, and
/// an unlink tidying up after itself. These are receipt images of somebody's spending, so
/// a row that goes must not leave its picture behind.
/// </para>
/// </remarks>
public interface IReceiptFiles
{
    /// <summary>
    /// Deletes each object, carrying on past any that storage will not delete. Never
    /// throws: the rows are already gone, so there is nothing left to undo.
    /// </summary>
    Task DeleteAsync(IEnumerable<string> objectKeys, string why, CancellationToken ct = default);
}

/// <inheritdoc cref="IReceiptFiles"/>
public sealed class ReceiptFiles(
    IAmazonS3 storage,
    IOptions<ReceiptStorageOptions> storageOptions,
    AppDbContext db,
    ILogger<ReceiptFiles> logger) : IReceiptFiles
{
    public async Task DeleteAsync(IEnumerable<string> objectKeys, string why, CancellationToken ct = default)
    {
        foreach (var objectKey in objectKeys)
        {
            try
            {
                var pending = await db.Set<PendingDataCleanup>()
                    .Where(row => row.Kind == "receipt" && row.Resource == objectKey).ToListAsync(ct);
                if (pending.Count == 0)
                {
                    var work = new PendingDataCleanup { Kind = "receipt", Resource = objectKey, Reason = "Receipt removed" };
                    db.Add(work);
                    await db.SaveChangesAsync(ct);
                    pending.Add(work);
                }
                await storage.DeleteObjectAsync(storageOptions.Value.BucketName, objectKey, ct);
                db.RemoveRange(pending);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception error)
            {
                logger.LogError(error, "{Message} {ObjectKey}", why, objectKey);
            }
        }
    }
}
