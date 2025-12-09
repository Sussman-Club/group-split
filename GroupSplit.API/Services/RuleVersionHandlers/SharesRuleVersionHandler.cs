using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class SharesRuleVersionHandler(AppDbContext dbContext, IGroupService groupService)
    : IRuleVersionHandler<SharesRuleVersion, SharesRuleVersionDto>
{
    public async Task<RuleVersionDto> ToDto(SharesRuleVersion version, CancellationToken ct)
    {
        var userShares = await (
                from ruleUser in dbContext.Entry(version).Collection(p => p.RuleUsers).Query()
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
        var users = await (from @group in await groupService.GetGroupById(groupId, ct)
            from user in @group.Users
            where dto.Shares.Keys.Contains(user.Id)
            select user).ToListAsync(ct);

        if (users.Count != dto.Shares.Count)
            throw new InvalidOperationException("Some users in the percentage rule do not exist.");

        var version = new SharesRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        };

        foreach (var (userId, shares) in dto.Shares)
        {
            if (shares is 0) continue;

            version.RuleUsers.Add(new SharesRuleUser
            {
                User = users.Single(u => u.Id == userId),
                Shares = shares
            });
        }

        return version;
    }

    public bool Equals(SharesRuleVersion existing, SharesRuleVersionDto incoming)
    {
        if (existing.RuleUsers.Count != incoming.Shares.Count)
            return false;

        foreach (var ru in existing.RuleUsers)
        {
            if (!incoming.Shares.TryGetValue(ru.User.Id, out var shares))
                return false;

            return ru.Shares == shares;
        }

        return true;
    }
}