using System.Buffers.Binary;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services;

/// <summary>Stores versioned merchant images and coordinates their merchant update; no role checks.</summary>
public sealed class MerchantImageService(
    IMerchantService merchants,
    IAmazonS3 storage,
    IOptions<ReceiptStorageOptions> storageOptions,
    ILogger<MerchantImageService> logger)
{
    // Reuse the configured object store, with a namespace separate from private receipts.
    private string Bucket => storageOptions.Value.BucketName;
    private static string Key(Guid id) => $"merchant-logos/{id:N}.png";

    public Task<MerchantResponse> SaveAsync(Guid? merchantId, string name, IFormFile file, CancellationToken ct) => SaveAsync(merchantId, name, file, null, null, ct);

    public async Task<MerchantResponse> SaveAsync(Guid? merchantId, string name, IFormFile file, Guid? actorId, Guid? expectedVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ValidationException(ErrorCodes.InvalidMerchant, "A merchant name is required, up to 128 characters.");
        }

        var previous = merchantId is { } id ? await merchants.GetDirectory(id, ct) : null;
        if (previous is not null && expectedVersion is not null && previous.Version != expectedVersion)
        {
            throw new ConflictException(ErrorCodes.Conflict, "This merchant changed. Reload before saving.");
        }

        var bytes = await ReadUploadAsync(file, ct);
        ValidatePng(bytes);
        var imageId = Guid.NewGuid();
        var logoUrl = $"{MerchantLogos.UploadedPath}{imageId:N}.png";
        var committed = false;

        try
        {
            await using var content = new MemoryStream(bytes, writable: false);
            await storage.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket,
                Key = Key(imageId),
                ContentType = "image/png",
                InputStream = content
            }, ct);

            MerchantResponse saved;
            if (merchantId is { } existing)
            {
                await merchants.UpdateDirectory(existing, new UpdateMerchantRequest { Name = name, LogoUrl = logoUrl, ExpectedVersion = previous!.Version }, actorId, ct);
                committed = true;
                saved = await merchants.GetDirectory(existing, ct);
            }
            else
            {
                saved = await merchants.CreateDirectory(new CreateMerchantRequest { Name = name, LogoUrl = logoUrl }, actorId, ct);
            }
            committed = true;
            return saved;
        }
        finally
        {
            if (!committed)
            {
                try
                {
                    await storage.DeleteObjectAsync(Bucket, Key(imageId), CancellationToken.None);
                }
                catch (Exception e) { logger.LogWarning(e, "Could not remove uncommitted merchant image {ImageId}", imageId); }
            }
        }
    }

    public async Task<byte[]> ReadAsync(Guid imageId, CancellationToken ct)
    {
        try
        {
            using var response = await storage.GetObjectAsync(Bucket, Key(imageId), ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound || e.ErrorCode == "NoSuchKey")
        {
            throw new NotFoundException(ErrorCodes.MerchantImageNotFound, "Merchant image not found.");
        }
    }

    private static async Task<byte[]> ReadUploadAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MerchantLogos.MaximumUploadBytes)
        {
            throw InvalidImage();
        }

        await using var source = file.OpenReadStream();
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length + read > MerchantLogos.MaximumUploadBytes)
            {
                throw InvalidImage();
            }

            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }

    internal static void ValidatePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> ending = [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
        if (bytes.Length < 45 || !bytes.AsSpan(0, 8).SequenceEqual(signature)
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13
            || !bytes.AsSpan(bytes.Length - 12).SequenceEqual(ending))
        {
            throw InvalidImage();
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > MerchantLogos.MaximumImageDimension || height is 0 or > MerchantLogos.MaximumImageDimension)
        {
            throw InvalidImage();
        }
    }

    private static ValidationException InvalidImage() => new(ErrorCodes.MerchantImageInvalid,
        "Upload a PNG image up to 512 by 512 pixels and 1 MB.");
}
