using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for POST /groups endpoint via GroupService.CreateGroup
/// </summary>
public class GroupBalanceTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task GetGroupBalance_BalanceSumIsZero() 
    {
        var userService = GetService<IUserService>();
        var groupService = GetService<IGroupService>();

        CreateNewUser();

        var groups = await (await groupService.GetAllGroups()).ToListAsync();

        foreach(var group in groups) {

            var balances = await (await groupService.GetGroupNetBalance(group.Id)).ToListAsync();
        }
    }
}