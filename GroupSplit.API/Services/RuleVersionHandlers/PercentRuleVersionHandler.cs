using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class PercentRuleVersionHandler(AppDbContext dbContext, IGroupService groupService) : IRuleVersionHandler
{
    public async Task<RuleDetailsResponse> GetRuleDetails(RuleVersion version, CancellationToken ct)
    {
        if (version is not PercentRuleVersion percentRuleVersion)
            throw new InvalidOperationException("Invalid rule version type.");

        await dbContext.Entry(percentRuleVersion)
            .Collection(p => p.RuleUsers)
            .Query()
            .Include(ru => ru.User)
            .LoadAsync(ct);

        return new RuleDetailsResponse
        {
            RuleId = percentRuleVersion.Rule.Id,
            RuleVersionId = percentRuleVersion.Id,
            Category = percentRuleVersion.Rule.Category,
            Version = new PercentRuleVersionDto
            {
                Percentages = percentRuleVersion.RuleUsers
                    .ToDictionary(
                        ru => ru.User.Id,
                        ru => (decimal)ru.Percentage
                    )
            }
        };
    }

    public async Task<RuleVersion> CreateEntity(Guid groupId, RuleVersionDto dto, CancellationToken ct)
    {
        if (dto is not PercentRuleVersionDto dtoPercent)
            throw new InvalidOperationException("Invalid dto type.");

        var total = dtoPercent.Percentages.Values.Sum();

        const decimal epsilon = 10e-3M;
        if (total + epsilon < 100 || total - epsilon > 100)
            throw new InvalidOperationException("Percentages must sum to 100%.");

        var userIds = dtoPercent.Percentages.Keys.ToList();

        var users = await (from @group in await groupService.GetGroupById(groupId, ct)
            from user in @group.Users
            where userIds.Contains(user.Id)
            select user).ToListAsync(ct);

        if (users.Count != dtoPercent.Percentages.Count)
            throw new InvalidOperationException("Some users in the percentage rule do not exist.");

        var version = new PercentRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        };

        foreach (var (userId, percent) in dtoPercent.Percentages)
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

    public bool Equals(RuleVersion existing, RuleVersionDto incoming)
    {
        if (existing is not PercentRuleVersion current || incoming is not PercentRuleVersionDto updated)
            return false;

        if (current.RuleUsers.Count != updated.Percentages.Count)
            return false;

        foreach (var ru in current.RuleUsers)
        {
            if (!updated.Percentages.TryGetValue(ru.User.Id, out var percent))
                return false;

            if (Math.Abs(ru.Percentage - (double)percent) > 0.001)
                return false;
        }

        return true;
    }
}