using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Moq;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// Who, if anybody, makes an expense undeletable by having left its group.
/// </summary>
/// <remarks>
/// The answer only decides what the delete button says; the server refuses regardless. So a
/// failed read is "nobody" -- the delete is offered and the server answers -- rather than a
/// second error on top of whatever went wrong.
/// </remarks>
public class DepartedDeleteGuardTest
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid Anabel = Guid.NewGuid();
    private static readonly Guid Omar = Guid.NewGuid();
    private static readonly Guid Sofia = Guid.NewGuid();

    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<IGroupsClient> _groups = new();

    public DepartedDeleteGuardTest()
    {
        // Anabel and Omar are in it; Sofia has left.
        _groups
            .Setup(client => client.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UserInfo(Anabel, "Anabel", "Benítez", null), new UserInfo(Omar, "Omar", "Rivero", null)]);
    }

    private DepartedDeleteGuard Guard() => new(_transactions.Object, _groups.Object);

    private Guid Expense(Guid paidBy, string paidByName, Guid? groupId, params (Guid Id, string Name)[] shares)
    {
        var id = Guid.NewGuid();

        _transactions
            .Setup(client => client.GetTransactionAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = id,
                Name = "Groceries",
                GroupId = groupId,
                PaidByUserId = paidBy,
                PaidByUserName = paidByName,
                Splits = [.. shares.Select(share => new TransactionSplitResponse(share.Id, share.Name, 10m))]
            });

        return id;
    }

    [Fact]
    public async Task Nobody_when_everybody_on_it_is_still_in_the_group()
    {
        var id = Expense(Anabel, "Anabel Benítez", GroupId, (Anabel, "Anabel Benítez"), (Omar, "Omar Rivero"));

        Assert.Null(await Guard().FirstLeaverOnAsync(id));
    }

    [Fact]
    public async Task The_payer_when_they_have_left()
    {
        var id = Expense(Sofia, "Sofia Kovács", GroupId, (Anabel, "Anabel Benítez"), (Sofia, "Sofia Kovács"));

        Assert.Equal("Sofia Kovács", await Guard().FirstLeaverOnAsync(id));
    }

    [Fact]
    public async Task Somebody_with_a_share_when_they_have_left()
    {
        var id = Expense(Anabel, "Anabel Benítez", GroupId, (Anabel, "Anabel Benítez"), (Sofia, "Sofia Kovács"));

        Assert.Equal("Sofia Kovács", await Guard().FirstLeaverOnAsync(id));
    }

    [Fact]
    public async Task Nobody_on_a_personal_expense_which_has_no_group_to_leave()
    {
        var id = Expense(Anabel, "Anabel Benítez", groupId: null, (Anabel, "Anabel Benítez"));

        Assert.Null(await Guard().FirstLeaverOnAsync(id));
        _groups.Verify(client => client.GetGroupMembersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Nobody_when_a_read_fails_so_the_server_decides()
    {
        var id = Guid.NewGuid();

        _transactions
            .Setup(client => client.GetTransactionAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("Not found", 404, null, new Dictionary<string, IEnumerable<string>>(), null));

        Assert.Null(await Guard().FirstLeaverOnAsync(id));
    }
}
