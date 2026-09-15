using Amazon.Extensions.NETCore.Setup;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Extensions;

/// <summary>Registers the API's S3-compatible client from Aspire's RustFS connection string.</summary>
public static class ReceiptStorageExtensions
{
    public static IServiceCollection AddReceiptStorage(this IServiceCollection services, IConfiguration configuration)
    {
        // AddRustFs("storage").AddBucket("receipts") exposes the bucket connection
        // to consumers under this generated resource name.
        var connectionString = configuration.GetConnectionString("storage-receipts");
        var options = configuration.GetAWSOptions();
        var bucketName = "receipts";

        if (connectionString is not null)
        {
            var fields = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => part[1], StringComparer.OrdinalIgnoreCase);

            options.Credentials = new BasicAWSCredentials(fields["AccessKey"], fields["SecretKey"]);
            options.DefaultClientConfig.ServiceURL = fields["Endpoint"];
            options.DefaultClientConfig.AuthenticationRegion = "us-east-1";
            options.DefaultClientConfig.ServiceSpecificSettings["ForcePathStyle"] = "true";
            if (fields.TryGetValue("Bucket", out var configuredBucket))
            {
                bucketName = configuredBucket;
            }
        }

        services.Configure<ReceiptStorageOptions>(settings => settings.BucketName = bucketName);

        return services
            .AddDefaultAWSOptions(options)
            .AddAWSService<IAmazonS3>();
    }
}

public sealed class ReceiptStorageOptions
{
    public string BucketName { get; set; } = "receipts";
}
