using GroupSplit.API.Services;
using GroupSplit.Shared;
using Moq;

// 'User' alone binds to the GroupSplit.API.Test.User namespace from in here.
using UserEntity = GroupSplit.Data.Entities.User;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// <see cref="DebtCalculationService"/> is the product: it turns each member's net
/// balance into the shortest list of payments that settles the group. It needs nothing
/// but the current user, so these run against the real algorithm with no database.
/// </summary>
public class DebtCalculationTests
{
    /// <summary>
    /// Builds the service as it sees the world for one member, since every answer it
    /// gives is from that member's point of view.
    /// </summary>
    private static IDebtCalculationService ServiceFor(Guid currentUserId)
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(user => user.User).Returns(new UserEntity { Id = currentUserId });
        return new DebtCalculationService(currentUser.Object);
    }

    private static GroupNetBalance Balance(Guid userId, string name, decimal balance) =>
        new() { UserId = userId, UserName = name, Balance = balance };

    [Fact]
    public async Task A_settled_group_produces_no_payments()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var result = await ServiceFor(alice).GetUserBalance(
            [Balance(alice, "Alice", 0m), Balance(bob, "Bob", 0m)]);

        Assert.Empty(result.OwedToYou);
        Assert.Empty(result.YouOwed);
    }

    [Fact]
    public async Task A_single_debtor_pays_the_single_creditor_the_whole_amount()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        // Alice is owed 25; Bob owes it.
        var balances = new[] { Balance(alice, "Alice", 25m), Balance(bob, "Bob", -25m) };

        var creditor = await ServiceFor(alice).GetUserBalance(balances);

        var owed = Assert.Single(creditor.OwedToYou);
        Assert.Equal(bob, owed.UserId);
        Assert.Equal("Bob", owed.UserName);
        Assert.Equal(25m, owed.Amount);
        Assert.Empty(creditor.YouOwed);
    }

    [Fact]
    public async Task The_debtor_sees_the_same_payment_from_the_other_side()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var balances = new[] { Balance(alice, "Alice", 25m), Balance(bob, "Bob", -25m) };

        var debtor = await ServiceFor(bob).GetUserBalance(balances);

        var owes = Assert.Single(debtor.YouOwed);
        Assert.Equal(alice, owes.UserId);
        Assert.Equal("Alice", owes.UserName);
        Assert.Equal(25m, owes.Amount);
        Assert.Empty(debtor.OwedToYou);
    }

    [Fact]
    public async Task One_debtor_against_many_creditors_is_split_across_them()
    {
        var debtor = Guid.NewGuid();
        var big = Guid.NewGuid();
        var small = Guid.NewGuid();

        // The largest creditor is settled first, so the 30 owed goes 20 then 10.
        var balances = new[]
        {
            Balance(debtor, "Debtor", -30m),
            Balance(big, "Big", 20m),
            Balance(small, "Small", 10m)
        };

        var result = await ServiceFor(debtor).GetUserBalance(balances);

        Assert.Empty(result.OwedToYou);
        Assert.Collection(result.YouOwed,
            first =>
            {
                Assert.Equal(big, first.UserId);
                Assert.Equal(20m, first.Amount);
            },
            second =>
            {
                Assert.Equal(small, second.UserId);
                Assert.Equal(10m, second.Amount);
            });
    }

    [Fact]
    public async Task Many_debtors_against_one_creditor_all_pay_the_same_person()
    {
        var creditor = Guid.NewGuid();
        var deep = Guid.NewGuid();
        var shallow = Guid.NewGuid();

        var balances = new[]
        {
            Balance(creditor, "Creditor", 30m),
            Balance(deep, "Deep", -20m),
            Balance(shallow, "Shallow", -10m)
        };

        var result = await ServiceFor(creditor).GetUserBalance(balances);

        Assert.Empty(result.YouOwed);
        Assert.Equal(30m, result.OwedToYou.Sum(debt => debt.Amount));
        Assert.Equal([deep, shallow], result.OwedToYou.Select(debt => debt.UserId));
    }

    /// <summary>
    /// The interesting case for a minimising algorithm: a member who is both paid by one
    /// person and pays another, because their balance is settled part-way through.
    /// </summary>
    [Fact]
    public async Task A_member_can_both_owe_and_be_owed()
    {
        var creditor = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var debtor = Guid.NewGuid();

        var balances = new[]
        {
            Balance(creditor, "Creditor", 50m),
            Balance(middle, "Middle", 10m),
            Balance(debtor, "Debtor", -60m)
        };

        var result = await ServiceFor(debtor).GetUserBalance(balances);

        Assert.Equal(60m, result.YouOwed.Sum(debt => debt.Amount));
        Assert.Equal([creditor, middle], result.YouOwed.Select(debt => debt.UserId));
    }

    [Fact]
    public async Task Fractional_amounts_settle_to_the_cent()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var carol = Guid.NewGuid();

        // 10.00 split three ways: one payer is owed 6.67, the two others owe 3.34 and 3.33.
        var balances = new[]
        {
            Balance(alice, "Alice", 6.67m),
            Balance(bob, "Bob", -3.34m),
            Balance(carol, "Carol", -3.33m)
        };

        var result = await ServiceFor(alice).GetUserBalance(balances);

        Assert.Equal(6.67m, result.OwedToYou.Sum(debt => debt.Amount));
        Assert.Equal([bob, carol], result.OwedToYou.Select(debt => debt.UserId));
    }

    /// <summary>
    /// A member who owes nothing and is owed nothing still gets an answer, rather than
    /// the missing-user error below.
    /// </summary>
    [Fact]
    public async Task A_member_who_is_square_is_still_in_the_result()
    {
        var square = Guid.NewGuid();
        var creditor = Guid.NewGuid();
        var debtor = Guid.NewGuid();

        var result = await ServiceFor(square).GetUserBalance(
        [
            Balance(square, "Square", 0m),
            Balance(creditor, "Creditor", 5m),
            Balance(debtor, "Debtor", -5m)
        ]);

        Assert.Empty(result.OwedToYou);
        Assert.Empty(result.YouOwed);
    }

    [Fact]
    public async Task A_current_user_absent_from_the_balances_is_an_error()
    {
        var stranger = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServiceFor(stranger).GetUserBalance(
                [Balance(alice, "Alice", 5m), Balance(bob, "Bob", -5m)]));

        Assert.Contains(stranger.ToString(), exception.Message);
    }

    /// <summary>
    /// The settlement walks the balances down to zero as it goes. It does that on copies,
    /// so the net balances handed back to the caller still say what each member's position
    /// actually is — if it ever worked on the originals every response would report a
    /// settled group.
    /// </summary>
    [Fact]
    public async Task Settling_does_not_zero_out_the_reported_net_balances()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var balances = new[] { Balance(alice, "Alice", 25m), Balance(bob, "Bob", -25m) };

        var result = await ServiceFor(alice).GetUserBalance(balances);

        Assert.Equal(25m, result.NetBalances.Single(net => net.UserId == alice).Balance);
        Assert.Equal(-25m, result.NetBalances.Single(net => net.UserId == bob).Balance);
    }
}
