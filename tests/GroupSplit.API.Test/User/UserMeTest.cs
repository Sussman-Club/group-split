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
        var userService = GetService<ICurrentUser>();

        // Act
        var result = userService.User;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(TestUserClaims.UserId, result.Identity.IdentityId);
        Assert.NotNull(result.PersonalGroup);
        Assert.Contains(result.PersonalGroup, result.Groups);

        // Verify user was saved to database
        var userInDb = await DbContext.Set<Data.Entities.User>()
            .FirstOrDefaultAsync(u => u.Identity.IdentityId == TestUserClaims.UserId, TestContext.Current.CancellationToken);

        Assert.NotNull(userInDb);
        Assert.Equal(result.Id, userInDb.Id);
    }

    [Fact]
    public async Task GetCurrentUser_MultipleCalls_ReturnsSameUser()
    {
        // Arrange
        var userService = GetService<ICurrentUser>();

        // Act
        var firstCall = userService.User;
        var secondCall = userService.User;

        // Assert
        Assert.Equal(firstCall.Id, secondCall.Id);
        Assert.Equal(firstCall.Identity.IdentityId, secondCall.Identity.IdentityId);
    }

    [Fact]
    public async Task GetCurrentUser_Email()
    {
        // Arrange
        var userService = GetService<ICurrentUser>();

        // Act
        var result = userService.User;

        // Assert
        Assert.Equal(TestUserClaims.Email, result.Email);
    }

    [Fact]
    public async Task GetCurrentUser_Different()
    {
        // Arrange
        var userService = GetService<ICurrentUser>();
        var firstUser = userService.User;

        // Arrange different user
        var secondUser = await CreateNewUser();

        // Assert
        Assert.NotEqual(firstUser.Id, secondUser.Id);
    }
}
