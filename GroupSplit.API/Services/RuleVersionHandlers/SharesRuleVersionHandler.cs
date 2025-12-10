using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class SharesRuleVersionHandler(AppDbContext dbContext)
    : IRuleVersionHandler<SharesRuleVersion, SharesRuleVersionDto>
{
    public async Task<RuleVersionDto> ToDto(SharesRuleVersion version, CancellationToken ct)
    {
        var userShares = await (
                from ruleUser in dbContext.Entry(version).Collection(p => p.SharedRuleUsers).Query()
                select new
                {
                    UserId = ruleUser.User.Id, ruleUser.Shares
                })
            .ToDictionaryAsync(ru => ru.UserId, ru => ru.Shares, cancellationToken: ct);

        return new SharesRuleVersionDto
        {
            Shares = userShares
        };
    }

    public async Task<RuleVersion> ToEntity(Guid groupId, SharesRuleVersionDto dto, CancellationToken ct)
    {
        var users = await (
            from @group in dbContext.Set<Group>()
            where @group.Id == groupId
            from user in @group.Users
            where dto.Shares.Keys.Contains(user.Id)
            select user
        ).ToListAsync(ct);

        if (users.Count != dto.Shares.Count)
            throw new InvalidOperationException("Some users in the shares rule do not exist.");

        var totalShares = dto.Shares.Values.Sum();

        var calculated = new List<(Guid UserId, int Shares, double Percentage)>();

        // Compute raw and rounded percentages
        foreach (var (userId, shares) in dto.Shares)
        {
            if (shares == 0) continue;

            var pct = Math.Round((double)shares / totalShares * 100, 2);
            calculated.Add((userId, shares, pct));
        }

        if (calculated.Count == 0)
            throw new InvalidOperationException("No users have shares.");

        // Fix rounding drift so total = 100
        var diff = 100 - calculated.Sum(x => x.Percentage);

        if (Math.Abs(diff) >= 0.01)
        {
            // Apply the difference to the last user to fix floating point rounding
            var last = calculated[^1];
            calculated[^1] = (last.UserId, last.Shares, last.Percentage + diff);
        }
        
        var version = new SharesRuleVersion
        {
            StartDateTime = DateTime.UtcNow
        };

        foreach (var c in calculated)
        {
            var user = users.Single(u => u.Id == c.UserId);

            version.SharedRuleUsers.Add(new SharesRuleUser
            {
                User = user,
                Shares = c.Shares
            });

            version.RuleUsers.Add(new PercentRuleUser
            {
                User = user,
                Percentage = c.Percentage
            });
        }

        return version;
    }

    public bool Equals(SharesRuleVersion existing, SharesRuleVersionDto incoming)
    {
        dbContext.Entry(existing)
            .Collection(r => r.SharedRuleUsers)
            .Query()
            .Include(ru => ru.User)
            .Load();

        if (existing.SharedRuleUsers.Count != incoming.Shares.Count)
            return false;

        foreach (var ru in existing.SharedRuleUsers)
        {
            if (!incoming.Shares.TryGetValue(ru.User.Id, out var shares))
                return false;

            if (ru.Shares != shares) return false;
        }

        return true;
    }
}