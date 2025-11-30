using GroupSplit.Data;
using GroupSplit.Data.Entities;

namespace GroupSplit.API.Services;

public interface IRulesService
{
    Task<IQueryable<RuleVersion>> List(CancellationToken cancellationToken = default);
}

public class RulesService(IUserService userService, AppDbContext dbContext) : IRulesService
{
    public async Task<IQueryable<RuleVersion>> List(CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();

        var query = from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
            from rule in @group.Rules
            from version in rule.Versions
            select version;

        return query;
    }
}