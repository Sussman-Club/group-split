using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// An expense divided the way the caller said, rather than the way its category says.
/// </summary>
/// <remarks>
/// This is the half of the reshape the wire had not caught up with: a split is a fact
/// about one expense, so somebody has to be able to state it -- "don't charge Omar for his
/// own birthday cake" is an edit to one expense, not a change to how the group splits
/// groceries forever.
/// <para>
/// The invariant underneath every test here is that the shares sum to the amount. Nothing
/// re-derives the division once it is stored, so a set that does not sum makes every
/// balance in the group wrong with nothing to catch it -- which is why the API refuses one
/// rather than quietly moving the difference onto somebody.
/// </para>
/// </remarks>
public class StatedSplitTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private async Task<(Guid GroupId, Guid Self, Guid Other)> GroupOfTwo()
    {
        var groups = GetService<IGroupService>();
        var self = GetService<ICurrentUser>().User.Id;

        var group = await groups.CreateGroup(
            new CreateGroupRequest { Name = "Trip" }, TestContext.Current.CancellationToken);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        return (group.Id, self, other.Id);
    }

    private static SplitInput Share(Guid userId, decimal amount) =>
        new() { UserId = userId, Amount = amount };

    private Task<List<TransactionSplit>> SplitsOf(Guid transactionId) =>
        DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == transactionId)
            .ToListAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Read through a scope of its own, so the answer is what was saved rather than what
    /// a failed write left sitting in the tracker.
    /// </summary>
    private async Task<List<TransactionSplit>> StoredSplitsOf(Guid transactionId)
    {
        using var scope = ServiceProvider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<TransactionSplit>()
            .Where(split => split.TransactionId == transactionId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private Task<Expense> Create(Guid groupId, decimal amount, IReadOnlyList<SplitInput>? splits,
        Guid? categoryId = null, Guid? paidBy = null) =>
        GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            PaidByUserId = paidBy,
            Splits = splits
        }, TestContext.Current.CancellationToken).AsTask();

    // ---- Creating with a stated division ------------------------------------------------

    [Fact]
    public async Task Stated_shares_are_stored_exactly_as_they_were_given()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var created = await Create(groupId, 100m, [Share(self, 30m), Share(other, 70m)]);

        var splits = await SplitsOf(created.Id);

        Assert.Equal(2, splits.Count);
        Assert.Equal(30m, splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(70m, splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// The point of stating them: the category's rule says one thing and this expense says
    /// another, and the expense wins without the rule changing for anybody else.
    /// </summary>
    [Fact]
    public async Task Stated_shares_override_the_category_without_changing_it()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var categoryId = await CreateCategory(groupId, "Cake", new PercentSplitRuleDto
        {
            Percentages = new Dictionary<Guid, decimal> { [self] = 50m, [other] = 50m }
        });

        var created = await Create(groupId, 40m, [Share(self, 40m)], categoryId);

        var split = Assert.Single(await SplitsOf(created.Id));
        Assert.Equal(self, split.UserId);
        Assert.Equal(40m, split.Amount);

        // The next expense in that category still divides the way the category says.
        var byTheRule = await Create(groupId, 40m, splits: null, categoryId);

        Assert.All(await SplitsOf(byTheRule.Id), share => Assert.Equal(20m, share.Amount));
    }

    /// <summary>
    /// Somebody may be owed money back on a single expense, so a share is allowed to be
    /// negative. It is the total that has to come out right, not each part.
    /// </summary>
    [Fact]
    public async Task A_share_may_be_negative_as_long_as_the_total_is_right()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var created = await Create(groupId, 50m, [Share(self, 60m), Share(other, -10m)]);

        var splits = await SplitsOf(created.Id);

        Assert.Equal(50m, splits.Sum(split => split.Amount));
        Assert.Equal(-10m, splits.Single(split => split.UserId == other).Amount);
    }

    [Fact]
    public async Task Naming_nobody_at_all_still_divides_by_the_category()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var created = await Create(groupId, 10m, splits: null);

        var splits = await SplitsOf(created.Id);

        Assert.Equal(2, splits.Count);
        Assert.All(splits, split => Assert.Equal(5m, split.Amount));
    }

    // ---- What is refused ----------------------------------------------------------------

    /// <summary>
    /// The invariant everything else rests on, and the only new way to break it that
    /// putting splits on the wire introduces.
    /// </summary>
    [Fact]
    public async Task Shares_that_do_not_add_up_are_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var failure = await Assert.ThrowsAsync<UnprocessableException>(() =>
            Create(groupId, 100m, [Share(self, 30m), Share(other, 60m)]));

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, failure.Code);
        Assert.Equal(422, failure.Status);
    }

    /// <summary>
    /// A dialog should be able to say "10.00 left to assign" rather than making somebody
    /// add the column up themselves, so the shortfall travels with the refusal.
    /// </summary>
    [Fact]
    public async Task The_refusal_names_the_shortfall()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var failure = await Assert.ThrowsAsync<UnprocessableException>(() =>
            Create(groupId, 100m, [Share(self, 30m), Share(other, 60m)]));

        Assert.Equal(100m, failure.Extensions["amount"]);
        Assert.Equal(90m, failure.Extensions["splitTotal"]);
        Assert.Equal(10m, failure.Extensions["difference"]);
    }

    /// <summary>
    /// Off by a cent is the case worth pinning: it is what a client doing its own division
    /// gets wrong, and rounding it away here is how a cent goes missing from a balance.
    /// </summary>
    [Fact]
    public async Task Being_off_by_a_single_cent_is_still_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var failure = await Assert.ThrowsAsync<UnprocessableException>(() =>
            Create(groupId, 10m, [Share(self, 3.33m), Share(other, 6.66m)]));

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, failure.Code);
    }

    [Fact]
    public async Task A_share_for_somebody_outside_the_group_is_refused()
    {
        var (groupId, self, _) = await GroupOfTwo();
        var stranger = await CreateNewUser();

        var failure = await Assert.ThrowsAsync<ConflictException>(() =>
            Create(groupId, 100m, [Share(self, 50m), Share(stranger.Id, 50m)]));

        Assert.Equal(ErrorCodes.SplitUserNotInGroup, failure.Code);
    }

    [Fact]
    public async Task Naming_the_same_person_twice_is_refused()
    {
        var (groupId, self, _) = await GroupOfTwo();

        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            Create(groupId, 100m, [Share(self, 50m), Share(self, 50m)]));

        Assert.Equal(ErrorCodes.SplitsInvalid, failure.Code);
    }

    /// <summary>
    /// An empty list is not the same as sending none. Sending none asks for the category's
    /// division; sending an empty one asks for an expense nobody owes anything on, which
    /// cannot sum to a non-zero amount and is far more likely to be a client bug.
    /// </summary>
    [Fact]
    public async Task An_empty_list_of_shares_is_refused_rather_than_read_as_no_opinion()
    {
        var (groupId, _, _) = await GroupOfTwo();

        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            Create(groupId, 100m, []));

        Assert.Equal(ErrorCodes.SplitsInvalid, failure.Code);
    }

    [Fact]
    public async Task A_refused_expense_is_not_stored_at_all()
    {
        var (groupId, self, other) = await GroupOfTwo();

        await Assert.ThrowsAsync<UnprocessableException>(() =>
            Create(groupId, 100m, [Share(self, 30m), Share(other, 60m)]));

        using var scope = ServiceProvider.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.False(await stored.Set<Data.Entities.Transaction>()
            .AnyAsync(TestContext.Current.CancellationToken));
    }

    // ---- Editing -------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_may_state_the_shares_and_they_replace_the_old_ones()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var created = await Create(groupId, 100m, splits: null);

        var transactions = GetService<ITransactionService>();
        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(model);

        model.Splits = [Share(self, 90m), Share(other, 10m)];

        await transactions.Update(created.Id, model, TestContext.Current.CancellationToken);

        var splits = await StoredSplitsOf(created.Id);

        Assert.Equal(2, splits.Count);
        Assert.Equal(90m, splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(10m, splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// The edit model carries the stored shares so that an ordinary edit preserves them.
    /// </summary>
    [Fact]
    public async Task The_edit_model_carries_stored_shares()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var created = await Create(groupId, 100m, splits: [Share(self, 70m), Share(other, 30m)]);

        var transactions = GetService<ITransactionService>();
        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(model);
        Assert.NotNull(model.Splits);
        Assert.Equal(2, model.Splits.Count);
        Assert.Equal(70m, model.Splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(30m, model.Splits.Single(split => split.UserId == other).Amount);

        model.Name = "Updated Name";
        await transactions.Update(created.Id, model, TestContext.Current.CancellationToken);

        var splits = await StoredSplitsOf(created.Id);

        Assert.Equal(100m, splits.Sum(split => split.Amount));
        Assert.Equal(70m, splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(30m, splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// Against the new amount, not the old one. An edit that changes both has to be checked
    /// against what it is becoming.
    /// </summary>
    [Fact]
    public async Task Stated_shares_on_an_edit_are_checked_against_the_new_amount()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var created = await Create(groupId, 100m, splits: null);

        var transactions = GetService<ITransactionService>();
        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(model);

        // The old amount's shares against the new amount: refused.
        model.Amount = 120m;
        model.Splits = [Share(self, 50m), Share(other, 50m)];

        var failure = await Assert.ThrowsAsync<UnprocessableException>(() =>
            transactions.Update(created.Id, model, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, failure.Code);

        // The stored expense is untouched by the attempt.
        var splits = await StoredSplitsOf(created.Id);
        Assert.Equal(100m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// A member who has left is no longer somebody a share can be given to, which is the
    /// membership check doing its work on the edit path as well as the create one.
    /// </summary>
    [Fact]
    public async Task An_edit_cannot_hand_a_share_to_somebody_outside_the_group()
    {
        var (groupId, self, _) = await GroupOfTwo();
        var stranger = await CreateNewUser();

        var created = await Create(groupId, 100m, splits: null);

        var transactions = GetService<ITransactionService>();
        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(model);

        model.Splits = [Share(self, 50m), Share(stranger.Id, 50m)];

        var failure = await Assert.ThrowsAsync<ConflictException>(() =>
            transactions.Update(created.Id, model, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(ErrorCodes.SplitUserNotInGroup, failure.Code);
    }

    /// <summary>
    /// Splits are the group's balances, so a stated division has to leave them summing to
    /// zero the way a computed one does.
    /// </summary>
    [Fact]
    public async Task A_stated_division_still_leaves_the_groups_balances_summing_to_zero()
    {
        var (groupId, self, other) = await GroupOfTwo();

        await Create(groupId, 100m, [Share(self, 30m), Share(other, 70m)], paidBy: self);
        await Create(groupId, 41.67m, [Share(self, 0m), Share(other, 41.67m)], paidBy: other);

        var balances = await (await GetService<IGroupService>()
                .GetGroupNetBalance(groupId, TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0m, balances.Sum(balance => balance.AmountPaid - balance.AmountOwed));
    }
}
