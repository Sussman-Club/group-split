using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.User;

/// <summary>
/// Tests for the /users/me endpoint via UserService
/// </summary>
public class UserMeTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{

    [Fact]
    public async Task GetCurrentUser_WhenUserDoesNotExist_CreatesNewUserWithPersonalGroup()
    {
        // Arrange
        var userService = GetService<IUserService>();

        // Act
        var result = await userService.GetCurrentUser();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(TestUserId, result.Identity.IdentityId);
        Assert.NotNull(result.PersonalGroup);
        Assert.Contains(result.PersonalGroup, result.Groups);

        // Verify user was saved to database
        var userInDb = await DbContext.Set<GroupSplit.Data.Entities.User>()
            .FirstOrDefaultAsync(u => u.Identity.IdentityId == TestUserId, TestContext.Current.CancellationToken);

        Assert.NotNull(userInDb);
        Assert.Equal(result.Id, userInDb.Id);
    }

    [Fact]
    public async Task GetCurrentUser_MultipleCalls_ReturnsSameUser()
    {
        // Arrange
        var userService = GetService<IUserService>();

        // Act
        var firstCall = await userService.GetCurrentUser();
        var secondCall = await userService.GetCurrentUser();

        // Assert
        Assert.Equal(firstCall.Id, secondCall.Id);
        Assert.Equal(firstCall.Identity.IdentityId, secondCall.Identity.IdentityId);
    }
}
