using Amazon.S3;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services.BackgroundJobs;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services;

public interface IReceiptFiles
{
    /// <summary>Stages durable deletion with the metadata removal; the caller saves both.</summary>
    void Enqueue(IEnumerable<string> objectKeys);

    /// <summary>Best-effort immediate removal after saving. The durable worker verifies removal later.</summary>
    Task DeleteAsync(IEnumerable<string> objectKeys, string why, CancellationToken ct = default);
}

public sealed class ReceiptFiles(
    IAmazonS3 storage,
    IOptions<ReceiptStorageOptions> storageOptions,
    IBackgroundJobQueue jobs,
    ILogger<ReceiptFiles> logger) : IReceiptFiles
{
    public void Enqueue(IEnumerable<string> objectKeys)
    {
        foreach (var key in objectKeys.Distinct()) jobs.Enqueue(new DeleteReceiptFile(key));
    }

    public async Task DeleteAsync(IEnumerable<string> objectKeys, string why, CancellationToken ct = default)
    {
        foreach (var key in objectKeys.Distinct())
        {
            try { await storage.DeleteObjectAsync(storageOptions.Value.BucketName, key, ct); }
            catch (Exception error)
            {
                logger.LogWarning("Receipt deletion deferred: {Reason} ({ErrorType})", why, error.GetType().Name);
            }
        }
    }
}
