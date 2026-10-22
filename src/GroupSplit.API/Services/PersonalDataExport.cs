using System.Data;
using GroupSplit.API.Services.PersonalData;
using GroupSplit.Data;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IPersonalDataExport
{
    Task<Stream> Create(CancellationToken ct);
}

/// <summary>Coordinates one consistent read and a complete archive before HTTP delivery.</summary>
public sealed class PersonalDataExport(AppDbContext db, IPersonalDataReader reader, IPersonalDataArchiveWriter writer) : IPersonalDataExport
{
    public Task<Stream> Create(CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(() => CreateSnapshot(ct));

    private async Task<Stream> CreateSnapshot(CancellationToken ct)
    {
        await using var snapshot = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var records = await reader.Read(ct);
        var archive = await writer.Write(records, ct);
        try
        {
            if (snapshot is not null) await snapshot.CommitAsync(ct);
            return archive;
        }
        catch
        {
            await archive.DisposeAsync();
            throw;
        }
    }
}
