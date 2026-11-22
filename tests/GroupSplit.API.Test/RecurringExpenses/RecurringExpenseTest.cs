using GroupSplit.API.Services;
using GroupSplit.API.Services.RecurringExpenses;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GroupSplit.API.Test.RecurringExpenses;

/// <summary>
/// Expenses that repeat on a schedule (#546): each occurrence created once on its day by the
/// hourly job, as an ordinary expense, and changes reaching only the ones still to come.
/// </summary>
public class RecurringExpenseTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeTimeProvider _clock = new(At(2026, 9, 6));

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<TimeProvider>(_clock);

    [Fact]
    public async Task A_monthly_one_creates_one_expense_on_its_day_with_its_details()
    {
        var (group, friend) = await GroupWithFriend();
        var food = await CreateEvenCategory(group, "Housing");
        var rent = await Recurring.Create(Rent(group, new DateOnly(2026, 9, 10)) with
        {
            PaidByUserId = friend.Id,
            CategoryId = food
        }, Ct);

        await RunOn(2026, 9, 9);
        Assert.Empty(await Occurrences(group));

        await RunOn(2026, 9, 10);
        await RunOn(2026, 9, 10);

        var created = Assert.Single(await Occurrences(group));
        Assert.Equal("Rent", created.Name);
        Assert.Equal(900m, created.Amount);
        Assert.Equal(friend.Id, created.UserId);
        Assert.Equal(food, created.CategoryId);
        Assert.Equal(new DateOnly(2026, 9, 10), Day(created));
        Assert.Equal(TransactionSource.Schedule, created.Source);
        Assert.Equal(Me.Id, created.EnteredById);
        Assert.Equal(2, created.Splits.Count);

        await RunOn(2026, 10, 10);
        Assert.Equal(2, (await Occurrences(group)).Count);
        Assert.Equal(new DateOnly(2026, 11, 10), (await Recurring.InGroup(group, Ct)).Single(r => r.Id == rent.Id).NextOn);
    }

    [Fact]
    public async Task A_day_the_month_does_not_have_falls_on_its_last_day()
    {
        _clock.SetUtcNow(At(2027, 1, 1));
        var (group, _) = await GroupWithFriend();
        await Recurring.Create(Rent(group, new DateOnly(2027, 1, 31)), Ct);

        await RunOn(2027, 1, 31);
        await RunOn(2027, 2, 28);
        await RunOn(2027, 3, 31);

        Assert.Equal(
            [new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31)],
            (await Occurrences(group)).Select(Day).Order());
    }

    [Theory]
    [InlineData(RepeatFrequency.Weekly, "2026-09-10", "2026-10-01", "2026-10-01")]
    [InlineData(RepeatFrequency.Monthly, "2026-01-31", "2026-02-01", "2026-02-28")]
    [InlineData(RepeatFrequency.Monthly, "2026-01-31", "2026-03-01", "2026-03-31")]
    [InlineData(RepeatFrequency.Yearly, "2024-02-29", "2025-01-01", "2025-02-28")]
    [InlineData(RepeatFrequency.Yearly, "2024-02-29", "2028-01-01", "2028-02-29")]
    public void The_next_day_is_counted_from_the_first(RepeatFrequency frequency, string startsOn, string from, string expected) =>
        Assert.Equal(DateOnly.Parse(expected),
            Recurrence.OnOrAfter(DateOnly.Parse(startsOn), frequency, DateOnly.Parse(from)));

    [Fact]
    public async Task Days_missed_while_nothing_ran_are_all_created_on_their_own_days()
    {
        var (group, _) = await GroupWithFriend();
        await Recurring.Create(Rent(group, new DateOnly(2026, 9, 10)) with { Frequency = RepeatFrequency.Weekly }, Ct);

        await RunOn(2026, 10, 1);

        Assert.Equal(
            [new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 24), new DateOnly(2026, 10, 1)],
            (await Occurrences(group)).Select(Day).Order());
    }

    [Fact]
    public async Task Pausing_creates_nothing_and_resuming_skips_the_paused_days()
    {
        var (group, _) = await GroupWithFriend();
        var rent = await Recurring.Create(Rent(group, new DateOnly(2026, 9, 10)), Ct);
        await RunOn(2026, 9, 10);

        await Recurring.Pause(rent.Id, Ct);
        await RunOn(2026, 11, 20);
        Assert.Single(await Occurrences(group));

        var resumed = await Recurring.Resume(rent.Id, Ct);

        Assert.False(resumed.Paused);
        Assert.Equal(new DateOnly(2026, 12, 10), resumed.NextOn);
    }

    [Fact]
    public async Task Stopping_creates_nothing_further_and_leaves_the_past_ones()
    {
        var (group, _) = await GroupWithFriend();
        var rent = await Recurring.Create(Rent(group, new DateOnly(2026, 9, 10)), Ct);
        await RunOn(2026, 9, 10);

        await Recurring.Stop(rent.Id, Ct);
        await RunOn(2026, 12, 10);

        Assert.Single(await Occurrences(group));
        Assert.Empty(await Recurring.InGroup(group, Ct));
        await Assert.ThrowsAsync<GroupSplit.API.Errors.NotFoundException>(() => Recurring.Resume(rent.Id, Ct));
    }

    [Fact]
    public async Task A_change_reaches_the_occurrences_still_to_come_only()
    {
        var (group, _) = await GroupWithFriend();
        var rent = await Recurring.Create(Rent(group, new DateOnly(2026, 9, 10)), Ct);
        await RunOn(2026, 9, 10);

        await Recurring.Update(rent.Id, rent.Details with { Amount = 950 }, Ct);
        await RunOn(2026, 10, 10);

        Assert.Equal([900m, 950m], (await Occurrences(group)).OrderBy(e => e.DateTime).Select(e => e.Amount));
    }

    /// <summary>
    /// The payer leaves: the day is not created, the reason is kept, and saving it again with
    /// somebody still in the group creates it on its own day.
    /// </summary>
    [Fact]
    public async Task A_payer_who_left_holds_the_day_back_and_says_why_until_it_is_fixed()
    {
        var (group, friend) = await GroupWithFriend();
        var rent = await Recurring.Create(Rent(group, new DateOnly(2026, 9, 10)) with { PaidByUserId = friend.Id }, Ct);

        var membership = await DbContext.Set<GroupMembership>()
            .SingleAsync(row => row.GroupId == group && row.UserId == friend.Id, Ct);
        membership.LeftAt = DateTimeOffset.UtcNow;
        membership.LeftReason = LeftReason.Left;
        await DbContext.SaveChangesAsync(Ct);

        await RunOn(2026, 9, 12);

        Assert.Empty(await Occurrences(group));
        var held = (await Recurring.InGroup(group, Ct)).Single();
        Assert.NotNull(held.Problem);
        Assert.Equal(new DateOnly(2026, 9, 10), held.NextOn);

        await Recurring.Update(rent.Id, held.Details with { PaidByUserId = Me.Id }, Ct);
        await RunOn(2026, 9, 12);

        var created = Assert.Single(await Occurrences(group));
        Assert.Equal(new DateOnly(2026, 9, 10), Day(created));
        Assert.Null((await Recurring.InGroup(group, Ct)).Single().Problem);
    }

    [Fact]
    public async Task A_start_in_the_past_begins_from_today_and_fills_nothing_in()
    {
        var (group, _) = await GroupWithFriend();

        var rent = await Recurring.Create(Rent(group, new DateOnly(2026, 6, 1)), Ct);

        Assert.Equal(new DateOnly(2026, 10, 1), rent.NextOn);
    }

    [Fact]
    public async Task What_would_be_refused_on_its_day_is_refused_when_set_up()
    {
        var (group, _) = await GroupWithFriend();
        var stranger = Guid.NewGuid();

        await Assert.ThrowsAsync<GroupSplit.API.Errors.ConflictException>(() =>
            Recurring.Create(Rent(group, new DateOnly(2026, 9, 10)) with { PaidByUserId = stranger }, Ct));
    }

    /// <summary>
    /// The expense is the first occurrence; the next falls a month after it. Shares typed in
    /// are kept as typed.
    /// </summary>
    [Fact]
    public async Task An_existing_expense_repeats_from_the_next_month_with_its_typed_shares()
    {
        var (group, friend) = await GroupWithFriend();
        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            Name = "Internet",
            Amount = 40,
            DateTime = At(2026, 9, 3),
            Splits = [new SplitInput { UserId = Me.Id, Amount = 10 }, new SplitInput { UserId = friend.Id, Amount = 30 }]
        }, Ct);

        var repeating = await Recurring.Repeat(expense.Id, new RepeatExpenseRequest(RepeatFrequency.Monthly), Ct);

        Assert.Equal(new DateOnly(2026, 10, 3), repeating.NextOn);
        Assert.Equal(30m, repeating.Details.Splits!.Single(split => split.UserId == friend.Id).Amount);

        await RunOn(2026, 10, 3);

        var next = (await Occurrences(group)).Single();
        Assert.Equal(30m, next.Splits.Single(split => split.UserId == friend.Id).Amount);
    }

    [Fact]
    public async Task An_existing_expense_divided_as_usual_repeats_without_fixed_shares()
    {
        var (group, _) = await GroupWithFriend();
        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            Name = "Internet",
            Amount = 40,
            DateTime = At(2026, 9, 3)
        }, Ct);

        var repeating = await Recurring.Repeat(expense.Id, new RepeatExpenseRequest(RepeatFrequency.Monthly), Ct);

        Assert.Null(repeating.Details.Splits);
        Assert.Null(repeating.Details.SplitRuleId);
    }

    [Fact]
    public async Task Another_groups_repeating_expenses_are_not_the_callers()
    {
        var (group, _) = await GroupWithFriend();
        var rent = await Recurring.Create(Rent(group, new DateOnly(2026, 9, 10)), Ct);

        using var scope = ServiceProvider.CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);
        var outsider = scope.ServiceProvider.GetRequiredService<IRecurringExpenseService>();

        await Assert.ThrowsAsync<GroupSplit.API.Errors.NotFoundException>(() => outsider.InGroup(group, Ct));
        await Assert.ThrowsAsync<GroupSplit.API.Errors.NotFoundException>(() => outsider.Pause(rent.Id, Ct));
    }

    // ---- setup -----------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 13, 0, 0, TimeSpan.Zero);

    private static DateOnly Day(Expense expense) => DateOnly.FromDateTime(expense.DateTime.UtcDateTime);

    private IRecurringExpenseService Recurring => GetService<IRecurringExpenseService>();

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private static RecurringExpenseRequest Rent(Guid group, DateOnly startsOn) => new()
    {
        GroupId = group,
        Name = "Rent",
        Amount = 900,
        Frequency = RepeatFrequency.Monthly,
        StartsOn = startsOn
    };

    private async Task RunOn(int year, int month, int day)
    {
        _clock.SetUtcNow(At(year, month, day));
        using var scope = ServiceProvider.CreateScope();
        await ActivatorUtilities.CreateInstance<CreateDueRecurringExpensesHandler>(scope.ServiceProvider)
            .HandleAsync(new CreateDueRecurringExpenses(), Ct);

        // The job saves in scopes of its own, as it does in the app; the test's long-lived context
        // would otherwise hand the service a row from before the run.
        foreach (var entry in DbContext.ChangeTracker.Entries<RecurringExpense>().ToList())
        {
            await entry.ReloadAsync(Ct);
        }
    }

    private async Task<List<Expense>> Occurrences(Guid group)
    {
        using var scope = ServiceProvider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Data.AppDbContext>().Set<Expense>().AsNoTracking()
            .Include(expense => expense.Splits)
            .Where(expense => expense.GroupId == group && expense.Source == TransactionSource.Schedule)
            .ToListAsync(Ct);
    }

    private async Task<(Guid Group, Data.Entities.User Friend)> GroupWithFriend()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var friend = await CreateNewUser();
        await JoinGroup(group.Id, friend);
        return (group.Id, friend);
    }
}
