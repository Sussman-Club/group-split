using GroupSplit.API.Services.Banking.Suggestions;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Where an expense at a merchant usually goes: what the caller did last time, or failing
/// that, the classifier's guess.
/// </summary>
public interface IMerchantDefaults
{
    /// <param name="currency">What the expense is in, for the classifier; history ignores it.</param>
    /// <param name="amount">The amount, for the classifier, when it is known yet.</param>
    /// <exception cref="Errors.NotFoundException">
    /// The caller has nothing at this merchant, as <c>GET /merchants/{id}</c> answers.
    /// </exception>
    Task<MerchantDefaultsResponse> For(Guid merchantId, string? currency, decimal? amount,
        CancellationToken ct = default);
}

/// <remarks>
/// History first because it is the answer the person already gave: "Mercadona" filed under
/// Groceries fifty times is Groceries the fifty-first, and choosing again from the same
/// lists is where categories drift. Only the caller's own reach counts -- an expense in a
/// group they are still in -- and a category that has since been archived is dropped
/// rather than offered, since nothing new can be filed under it.
/// <para>
/// The split comes with the category: an expense under a category divides by its rule. A
/// rule is suggested on its own only where the last expense had no usable category and
/// chose one for itself -- "all for Ana" -- and only a rule a new expense may still name.
/// </para>
/// </remarks>
public sealed class MerchantDefaults(
    ICurrentUser userContext,
    IGroupService groups,
    IMerchantService merchants,
    AppDbContext dbContext,
    IFilingSuggester suggester) : IMerchantDefaults
{
    public async Task<MerchantDefaultsResponse> For(Guid merchantId, string? currency, decimal? amount,
        CancellationToken ct = default)
    {
        // Through the merchant read, so a merged-away id resolves to the place it became and
        // a place the caller has nothing at is not found, the same as it is there.
        var merchant = await merchants.Get(merchantId, ct);
        var me = userContext.User.Id;
        var mine = await groups.GetAllGroups(ct);

        var last = await dbContext.Set<Expense>()
            .AsNoTracking()
            .Where(expense => expense.MerchantId == merchant.Id && mine.Any(group => group.Id == expense.GroupId))
            .OrderByDescending(expense => expense.DateTime)
            .ThenByDescending(expense => expense.Id)
            .Select(expense => new
            {
                expense.Id,
                expense.DateTime,
                expense.GroupId,
                GroupName = expense.Group.Name,
                Category = expense.Category == null
                    ? null
                    : new { expense.Category.Id, expense.Category.Name, expense.Category.ArchivedAt, expense.Category.DefaultSplitRuleId },
                Rule = expense.SplitRuleVersion == null
                    ? null
                    : new
                    {
                        expense.SplitRuleVersion.SplitRule.Id,
                        expense.SplitRuleVersion.SplitRule.Name,
                        GroupId = expense.SplitRuleVersion.SplitRule.Group.Id,
                        // Even is what no rule at all means, and dividing by the bill needs
                        // the bill; neither is a choice worth carrying to a new expense.
                        IsAChoice = !(expense.SplitRuleVersion is EvenSplitRuleVersion) &&
                                    !(expense.SplitRuleVersion is ItemizedSplitRuleVersion)
                    }
            })
            .FirstOrDefaultAsync(ct);

        if (last is null)
        {
            return new MerchantDefaultsResponse(currency is { Length: 3 }
                ? await suggester.SuggestMerchant(merchant.Name, amount ?? 0, currency, ct)
                : null);
        }

        var category = last.Category is { ArchivedAt: null } usable ? usable : null;

        // A rule it chose rather than one its category divided by: "all for Ana", or one
        // the group wrote.
        var rule = category is null && last.Rule is { IsAChoice: true } chosen && chosen.GroupId == last.GroupId &&
                   chosen.Id != last.Category?.DefaultSplitRuleId
            ? last.Rule
            : null;

        return new MerchantDefaultsResponse(
            new FilingDestinationSuggestion(last.GroupId, last.GroupName, last.GroupId == me,
                category?.Id, category?.Name, Confidence: 1, rule?.Id, rule?.Name),
            last.Id,
            last.DateTime);
    }
}
