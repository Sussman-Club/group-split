using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GroupSplit.API.Services.PersonalData;

public interface IPersonalDataArchiveWriter
{
    Task<Stream> Write(ExportRecords records, CancellationToken ct);
}

/// <summary>Packages a portable record set, spreadsheet views and original receipt files.</summary>
public sealed class PersonalDataArchiveWriter(IReceiptAttachmentService receiptFiles) : IPersonalDataArchiveWriter
{
    public async Task<Stream> Write(ExportRecords records, CancellationToken ct)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            BufferSize = 65536, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var output = new FileStream(Path.Combine(Path.GetTempPath(), $"groupsplit-export-{Guid.NewGuid():N}.zip"), options);
        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                var archive = new ExportArchive(zip);
                await archive.Write("data.json", stream => JsonSerializer.SerializeAsync(stream, records, ExportJson.Options, ct), ct);
                await archive.Write("schema.json", stream => JsonSerializer.SerializeAsync(stream, ExportJson.Schema(), ExportJson.Options, ct), ct);
                await archive.Write("README.txt", stream => stream.WriteAsync(Encoding.UTF8.GetBytes(ExportReadme.Text), ct).AsTask(), ct);
                await archive.Write("tables/transactions.csv", stream => WriteTransactions(stream, records.Transactions, ct), ct);
                await archive.Write("tables/splits.csv", stream => WriteShares(stream, records.Transactions, ct), ct);
                foreach (var attachment in records.Attachments)
                    await WriteReceipt(archive, attachment, ct);
                await archive.Complete(ct);
            }
            output.Position = 0;
            return output;
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    private async Task WriteReceipt(ExportArchive archive, ExportAttachment attachment, CancellationToken ct)
    {
        var receipt = attachment.ExpenseId is { } expenseId
            ? await receiptFiles.Download(expenseId, attachment.Id, ct)
            : await receiptFiles.DownloadBank(attachment.BankTransactionId!.Value, attachment.Id, ct);
        if (receipt.Content.LongLength != attachment.Length)
            throw new IOException("A receipt changed during export. Retry the export.");
        await archive.Write(attachment.Path, stream => stream.WriteAsync(receipt.Content, ct).AsTask(), ct);
    }

    private static async Task WriteTransactions(Stream stream, IAsyncEnumerable<ExportTransaction> transactions, CancellationToken ct)
    {
        await using var csv = CsvWriter(stream);
        await csv.WriteLineAsync("id,kind,groupId,payerId,name,description,amount,currency,dateTime".AsMemory(), ct);
        await foreach (var row in transactions.WithCancellation(ct))
            await csv.WriteLineAsync(ExportCsv.Row(row.Id, row.Kind, row.GroupId, row.UserId,
                row.Name, row.Description, row.Amount, row.Currency, row.DateTime).AsMemory(), ct);
    }

    private static async Task WriteShares(Stream stream, IAsyncEnumerable<ExportTransaction> transactions, CancellationToken ct)
    {
        await using var csv = CsvWriter(stream);
        await csv.WriteLineAsync("transactionId,userId,amount,currency".AsMemory(), ct);
        await foreach (var transaction in transactions.WithCancellation(ct))
            foreach (var share in transaction.Splits)
                await csv.WriteLineAsync(ExportCsv.Row(transaction.Id, share.UserId, share.Amount, transaction.Currency).AsMemory(), ct);
    }

    private static StreamWriter CsvWriter(Stream stream) =>
        new(stream, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" };
}
