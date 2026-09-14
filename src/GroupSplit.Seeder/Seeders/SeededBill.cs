using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// The bill behind a seeded expense, and the rules its lines divide by.
/// </summary>
/// <remarks>
/// A seed line names its rule by name rather than by id, because the ids are made here: the
/// first line to name "Items: Ana" creates that rule in the expense's group and every later
/// line in the group -- in this bill or another -- points at the same one. Written that way
/// so the demo's rules read as a group's real handful of rules rather than as one per line.
/// </remarks>
internal static class SeededBill
{
    public static async Task<Receipt> ForExpense(ReceiptSeedDto dto, Expense expense,
        AppDbContext db, ISplitRuleFactory factory, CancellationToken ct)
    {
        var receipt = new Receipt { Expense = expense, ExpenseId = expense.Id,
            Subtotal = dto.Items.Sum(i => i.Price), Tax = dto.Tax, Tip = dto.Tip,
            Total = dto.Items.Sum(i => i.Price) + dto.Tax + dto.Tip };
        foreach (var input in dto.Items)
        {
            SplitRuleVersion? version = null;
            if (input.SplitRule is { } definition)
            {
                var name = input.RuleName ?? throw new InvalidOperationException("A seeded item rule needs a name.");
                // The tracked ones first: a run seeds several bills before it saves, so a
                // rule this run has already made is in the change tracker and nowhere else,
                // and querying past it would make a second rule of the same name.
                var rule = db.Set<SplitRule>().Local.FirstOrDefault(r => r.Group.Id == expense.Group!.Id && r.Name == name)
                    ?? await db.Set<SplitRule>().Include(r => r.Group).Include(r => r.Versions)
                        .ThenInclude(v => (v as WeightedSplitRuleVersion)!.Participants)
                        .FirstOrDefaultAsync(r => r.Group.Id == expense.Group!.Id && r.Name == name, ct);
                if (rule is null)
                {
                    version = factory.FromDto(definition);
                    // Started long before any seeded expense, so every one of them falls
                    // inside this version's window and is divided by it rather than by
                    // whatever the rule said before it existed.
                    version.StartedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    rule = new SplitRule { Name = name, Group = expense.Group!, Versions = { version } };
                    db.Add(rule);
                }
                // Current rather than the one just made: where the rule was already there,
                // the seed file's definition is a description of it and not a new version of
                // it -- the line divides by what the group's rule says now.
                version = rule.Current;
            }
            receipt.Items.Add(new ReceiptItem { Name = input.Name, NormalizedName = input.Name.Trim().ToLowerInvariant(),
                Position = receipt.Items.Count, UnitPrice = decimal.Round(input.Price / input.Quantity, 2),
                Quantity = input.Quantity, TotalPrice = input.Price, TaxAmount = input.TaxAmount,
                SplitRuleVersion = version, SplitRuleVersionId = version?.Id });
        }
        return receipt;
    }
}
