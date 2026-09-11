using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Repayments between seeded members.
/// </summary>
/// <remarks>
/// Without these the demo data is all one direction: every balance only ever grows, the
/// Settle page's history is empty, and a group's ledger filtered to settlements shows
/// nothing at all. A group that has been running for a year and never squared up once is
/// not a profile anybody recognises.
/// <para>
/// It runs after the expenses so the dates interleave the way they would have happened,
/// and it writes through <see cref="TransferExtensions.SettlementBetween"/> rather than
/// constructing a row --
/// the factory is what guarantees the single split to the recipient that makes both
/// balances move.
/// </para>
/// </remarks>
[DependsOn(typeof(TransactionSeeder))]
[DependsOn(typeof(UserSeeder))]
public class SettlementSeeder(
    AppDbContext db,
    ILogger<SettlementSeeder> logger,
    ISeedDataSource<SettlementSeedDto> source)
    : AppDbContextSeeder<Transfer, SettlementSeedDto>(db, source, logger)
{
    protected override async Task<Transfer?> MapAsync(SettlementSeedDto dto,
        CancellationToken ct = default)
    {
        if (dto.FromUserId == dto.ToUserId)
            return null;

        var group = await DbContext.Set<Group>().FindAsync([dto.GroupId], ct);
        var from = await DbContext.Set<User>().FindAsync([dto.FromUserId], ct);
        var to = await DbContext.Set<User>().FindAsync([dto.ToUserId], ct);

        if (group is null || from is null || to is null)
            return null;

        return group.SettlementBetween(from, to, dto.Amount, dto.DateTime, dto.Description);
    }

    /// <summary>
    /// Matched on what identifies a repayment rather than on an id, because the factory
    /// that builds one does not take an id -- see <see cref="SettlementSeedDto"/>. Two
    /// people can settle the same amount twice, but not in the same group at the same
    /// instant, which is what makes this enough to re-run the seeder safely.
    /// </summary>
    protected override Task<bool> ExistsAsync(Transfer entity, CancellationToken ct) =>
        DbContext.Set<Transfer>().AnyAsync(existing =>
            existing.GroupId == entity.GroupId &&
            existing.UserId == entity.UserId &&
            existing.Amount == entity.Amount &&
            existing.DateTime == entity.DateTime, ct);
}
