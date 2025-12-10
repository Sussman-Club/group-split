using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class PercentRuleVersionHandler(AppDbContext dbContext, IGroupService groupService)
    : IRuleVersionHandler<PercentRuleVersion, PercentRuleVersionDto>
{
    public async Task<RuleVersionDto> ToDto(PercentRuleVersion version, CancellationToken ct)
    {
        var userPercentages = await (
                from ruleUser in dbContext.Entry(version).Collection(p => p.RuleUsers).Query()
                select new
                {
                    UserId = ruleUser.User.Id, ruleUser.Percentage
                })
            .ToDictionaryAsync(ru => ru.UserId, ru => (decimal)ru.Percentage, cancellationToken: ct);

        return new PercentRuleVersionDto
        {
            Percentages = userPercentages
        };
    }

    public async Task<RuleVersion> ToEntity(Guid groupId, PercentRuleVersionDto dto, CancellationToken ct)
    {
        var total = dto.Percentages.Values.Sum();

        const decimal epsilon = 10e-3M;
        if (total + epsilon < 100 || total - epsilon > 100)
            throw new InvalidOperationException("Percentages must sum to 100%.");

        var users = await (from @group in await groupService.GetGroupById(groupId, ct)
            from user in @group.Users
            where dto.Percentages.Keys.Contains(user.Id)
            select user).ToListAsync(ct);

        if (users.Count != dto.Percentages.Count)
            throw new InvalidOperationException("Some users in the percentage rule do not exist.");

        var version = new PercentRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        };

        foreach (var (userId, percent) in dto.Percentages)
        {
            if (percent is 0) continue;
            version.RuleUsers.Add(new PercentRuleUser
            {
                User = users.Single(u => u.Id == userId),
                Percentage = (double)percent
            });
        }

        return version;
    }

    public bool Equals(PercentRuleVersion existing, PercentRuleVersionDto incoming)
    {
        dbContext.Entry(existing)
            .Collection(r => r.RuleUsers)
            .Query()
            .Include(ru => ru.User)
            .Load();

        if (existing.RuleUsers.Count != incoming.Percentages.Count)
            return false;

        foreach (var ru in existing.RuleUsers)
        {
            if (!incoming.Percentages.TryGetValue(ru.User.Id, out var percent))
                return false;

            if (Math.Abs(ru.Percentage - (double)percent) > 0.001)
                return false;
        }

        return true;
    }
}