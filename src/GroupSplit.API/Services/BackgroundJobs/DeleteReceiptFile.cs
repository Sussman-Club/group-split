using Amazon.S3;
using GroupSplit.API.Extensions;
using GroupSplit.Jobs;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.BackgroundJobs;

/// <summary>Deleting an absent object is safe, including after a lost acknowledgement.</summary>
public sealed record DeleteReceiptFile(string ObjectKey) : IJob
{
    public const string Name = "delete-receipt-file";
    public override string ToString() => Name;
}

internal sealed class DeleteReceiptFileHandler(IAmazonS3 storage, IOptions<ReceiptStorageOptions> options)
    : IJobHandler<DeleteReceiptFile>
{
    public async ValueTask HandleAsync(DeleteReceiptFile job, CancellationToken cancellationToken) =>
        await storage.DeleteObjectAsync(options.Value.BucketName, job.ObjectKey, cancellationToken);
}

