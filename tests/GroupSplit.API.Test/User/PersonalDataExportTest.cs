using System.IO.Compression;
using System.Net;
using System.Text.Json;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using Moq;

namespace GroupSplit.API.Test.User;

public class PersonalDataExportTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Mock<IReceiptAttachmentService> _files = new();

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton(_files.Object);

    [Fact]
    public async Task Archive_preserves_exact_money_receipt_bytes_and_verifiable_file_inventory()
    {
        var me = GetService<ICurrentUser>().User;
        const string name = "=SUM(1,2)\n\"Dinner\"";
        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        { Name = name, Amount = 12.50m, DateTime = DateTimeOffset.UtcNow, PaidByUserId = me.Id }, Ct);
        var attachment = new ReceiptAttachment { ExpenseId = expense.Id, FileName = "../../outside.png",
            ObjectKey = "PRIVATE-OBJECT-KEY", ContentType = "image/png", Length = 3,
            UploadedByUserId = me.Id, UploadedAt = DateTimeOffset.UtcNow };
        DbContext.Add(attachment);
        await DbContext.SaveChangesAsync(Ct);
        _files.Setup(files => files.Download(expense.Id, attachment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptAttachmentDownload([1, 2, 3], "image/png", attachment.FileName));

        await using var stream = await GetService<IPersonalDataExport>().Create(Ct);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var data = await JsonDocument.ParseAsync(zip.GetEntry("data.json")!.Open(), cancellationToken: Ct);
        var transaction = Assert.Single(data.RootElement.GetProperty("transactions").EnumerateArray());
        Assert.Equal("12.50", transaction.GetProperty("amount").GetString());
        Assert.Equal(name, transaction.GetProperty("name").GetString());
        var file = Assert.Single(data.RootElement.GetProperty("attachments").EnumerateArray());
        Assert.Equal($"receipts/{attachment.Id}.png", file.GetProperty("path").GetString());
        Assert.DoesNotContain("PRIVATE-OBJECT-KEY", data.RootElement.GetRawText());
        using var csvReader = new StreamReader(zip.GetEntry("tables/transactions.csv")!.Open());
        var csv = await csvReader.ReadToEndAsync(Ct);
        Assert.Contains("\"'=SUM(1,2)\n\"\"Dinner\"\"\"", csv);
        Assert.Contains("\"12.50\"", csv);

        using var manifest = await JsonDocument.ParseAsync(zip.GetEntry("manifest.json")!.Open(), cancellationToken: Ct);
        var entries = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(zip.Entries.Count - 1, entries.Length);
        foreach (var entry in entries)
        {
            var archived = zip.GetEntry(entry.GetProperty("path").GetString()!)!;
            await using var content = archived.Open();
            Assert.Equal(archived.Length, entry.GetProperty("bytes").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(await SHA256.HashDataAsync(content, Ct)), entry.GetProperty("sha256").GetString());
        }
        using var schema = await JsonDocument.ParseAsync(zip.GetEntry("schema.json")!.Open(), cancellationToken: Ct);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        await using var image = zip.GetEntry($"receipts/{attachment.Id}.png")!.Open();
        using var bytes = new MemoryStream();
        await image.CopyToAsync(bytes, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
    }

    [Fact]
    public async Task Missing_receipt_aborts_the_export_instead_of_returning_an_incomplete_archive()
    {
        var me = GetService<ICurrentUser>().User;
        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        { Name = "Dinner", Amount = 12m, DateTime = DateTimeOffset.UtcNow, PaidByUserId = me.Id }, Ct);
        var file = new ReceiptAttachment { ExpenseId = expense.Id, FileName = "receipt.jpg", ObjectKey = "key",
            ContentType = "image/jpeg", Length = 10, UploadedByUserId = me.Id, UploadedAt = DateTimeOffset.UtcNow };
        DbContext.Add(file);
        await DbContext.SaveChangesAsync(Ct);
        _files.Setup(files => files.Download(expense.Id, file.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Missing object"));
        await Assert.ThrowsAsync<IOException>(() => GetService<IPersonalDataExport>().Create(Ct));
    }

    [Fact]
    public async Task Export_route_requires_authentication_and_disables_caching()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        using var anonymous = host.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/users/me/export", Ct)).StatusCode);
        var result = await host.Client.GetAsync("/users/me/export", Ct);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.Headers.CacheControl?.NoStore);
        Assert.Equal("application/zip", result.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Export_includes_visible_currency_and_shares_but_excludes_unrelated_records_and_credentials()
    {
        var me = GetService<ICurrentUser>().User;
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Visible" }, Ct);
        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        { GroupId = group.Id, Name = "Dinner", Amount = 12.50m, Currency = "USD", DateTime = DateTimeOffset.UtcNow, PaidByUserId = me.Id }, Ct);

        await using var scope = ServiceProvider.CreateAsyncScope();
        await InitializeCurrentUser(scope.ServiceProvider);
        var other = scope.ServiceProvider.GetRequiredService<ICurrentUser>().User;
        var hidden = await scope.ServiceProvider.GetRequiredService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Unrelated private group" }, Ct);
        DbContext.Add(new BankConnection { UserId = other.Id, Provider = "test", ProviderItemId = "private-item",
            InstitutionName = "Hidden bank", AccessTokenCiphertext = "NEVER-EXPORT-CREDENTIAL", LinkedAt = DateTimeOffset.UtcNow });
        await DbContext.SaveChangesAsync(Ct);

        await using var stream = await GetService<IPersonalDataExport>().Create(Ct);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var json = await JsonDocument.ParseAsync(archive.GetEntry("data.json")!.Open(), cancellationToken: Ct);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var record = Assert.Single(root.GetProperty("transactions").EnumerateArray());
        Assert.Equal(expense.Id, record.GetProperty("id").GetGuid());
        Assert.Equal("USD", record.GetProperty("currency").GetString());
        Assert.Single(record.GetProperty("splits").EnumerateArray());
        Assert.Empty(root.GetProperty("bankConnections").EnumerateArray());
        Assert.DoesNotContain(hidden.Id.ToString(), root.GetRawText());
        Assert.DoesNotContain("NEVER-EXPORT-CREDENTIAL", root.GetRawText());
        Assert.DoesNotContain("accessToken", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
