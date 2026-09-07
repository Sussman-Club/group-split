using GroupSplit.API.Services;
using GroupSplit.API.Errors;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

public class GroupSettleTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// One row, not two. A settlement used to be a matched pair -- <c>+amount</c> against
    /// the other member and <c>-amount</c> against the caller -- which had to be written
    /// together to mean anything, and which appeared in every expense list that forgot to
    /// exclude it. A transfer says the same thing once: the payer paid, and the single
    /// split names who they paid.
    /// </summary>
    [Fact]
    public async Task Settle_CreatesOneTransfer()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<ICurrentUser>();

        var currentUser = userService.User;

        // Create a new group with current user
        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Settle Group" },
            TestContext.Current.CancellationToken);

        // Add another member
        var otherUser = await CreateNewUser();

        await JoinGroup(group.Id, otherUser);

        var request = new SettleRequest
        {
            UserId = otherUser.Id,
            Amount = 50
        };

        // Act
        await groupService.Settle(
            group.Id,
            request,
            TestContext.Current.CancellationToken);

        var transfers = await DbContext.Set<Transfer>()
            .Include(transfer => transfer.User)
            .Include(transfer => transfer.Splits)
            .ThenInclude(split => split.User)
            .Where(transfer => transfer.GroupId == group.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        var transfer = Assert.Single(transfers);

        Assert.Equal(50, transfer.Amount);

        // The caller is the creditor recording that a debtor paid them, so the money moves
        // from the other member to the caller.
        Assert.Equal(otherUser.Id, transfer.User.Id);

        var split = Assert.Single(transfer.Splits);

        Assert.Equal(currentUser.Id, split.User.Id);
        Assert.Equal(50, split.Amount);

        // Nothing negative anywhere: the direction is carried by which side of the split
        // each person is on, not by the sign of an amount.
        Assert.True(transfer.Amount > 0);
    }

    /// <summary>
    /// A transfer needs two people. The old pair quietly accepted this and wrote a
    /// <c>+50</c> and a <c>-50</c> against the same member, which cancelled out and left
    /// two rows saying nothing.
    /// </summary>
    [Fact]
    public async Task Settle_WithYourself_Throws()
    {
        var groupService = GetService<IGroupService>();
        var currentUser = GetService<ICurrentUser>().User;

        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Settle Group" },
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            groupService.Settle(group.Id,
                new SettleRequest { UserId = currentUser.Id, Amount = 50 },
                TestContext.Current.CancellationToken));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementWithSelf, exception.Code);
    }

    [Fact]
    public async Task Settle_UserNotFoundInGroup_Throws()
    {
        // Arrange
        var groupService = GetService<IGroupService>();

        // Create group with only current user
        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Settle Group" },
            TestContext.Current.CancellationToken);

        var request = new SettleRequest
        {
            UserId = Guid.NewGuid(), // Not in the group
            Amount = 100
        };

        // Act + Assert
        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            groupService.Settle(group.Id, request,
                TestContext.Current.CancellationToken));

        Assert.Contains("User was not found", ex.Message);
    }

    [Fact]
    public async Task Settle_GroupNotFound_Throws()
    {
        // Arrange
        var groupService = GetService<IGroupService>();

        var request = new SettleRequest
        {
            UserId = Guid.NewGuid(),
            Amount = 10
        };

        // Act + Assert
        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            groupService.Settle(Guid.NewGuid(), request,
                TestContext.Current.CancellationToken));

        Assert.Contains("Group was not found", ex.Message);
    }
}