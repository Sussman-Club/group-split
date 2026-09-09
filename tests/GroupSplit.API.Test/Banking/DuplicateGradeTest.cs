using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// How strong a claim a suggestion is, and what follows from that.
/// </summary>
/// <remarks>
/// Measured over real spending, the amount-and-date test alone passes 11.5% of unrelated
/// pairs of expenses by one person -- so on a realistic week's typing, most rows arriving
/// raised a suggestion about money they had nothing to do with. What pins that down here is
/// the grade: the same money to the cent is a claim worth putting in front of somebody, and
/// a figure within a quarter is a question worth asking only of whoever files the row.
/// <para>
/// The refusal on filing is deliberately not graded. A tip added to a dinner is the
/// likeliest duplicate there is and it is only ever <see cref="MatchConfidence.Possible"/>,
/// so a guard that wanted a confident match would wave the commonest case through -- which
/// is the failure the whole feature exists to prevent.
/// </para>
/// </remarks>
public class DuplicateGradeTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();

    /// <summary>The day the dinner was eaten. Everything here is relative to it.</summary>
    private static readonly DateOnly Dinner = new(2026, 9, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInboxService Inbox => GetService<IInboxService>();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);

    // ---- the grades ----------------------------------------------------------------------

    [Fact]
    public async Task The_same_money_to_the_cent_is_a_confident_match()
    {
        await Expense("Dinner", 46.20m, Dinner);
        var row = await Row(46.20m, on: Dinner.AddDays(2));

        var match = Assert.Single(await Inbox.Matches(row.Id, Ct));

        Assert.Equal(MatchConfidence.Confident, match.Confidence);
    }

    [Fact]
    public async Task A_tip_added_after_the_receipt_is_a_possible_match_and_not_a_confident_one()
    {
        await Expense("Dinner", 40m, Dinner);
        var row = await Row(46m, on: Dinner.AddDays(2));

        var match = Assert.Single(await Inbox.Matches(row.Id, Ct));

        Assert.Equal(MatchConfidence.Possible, match.Confidence);
    }

    /// <summary>
    /// The shape that cost half of every suggestion the rule used to raise.
    /// </summary>
    /// <remarks>
    /// The tolerance is there for a tip, and a tip only makes the charge larger. A bank
    /// charging 14.99 against an expense of 17.43 is not a tip and is not a duplicate: it is
    /// the streaming subscription the seeded inbox offers against a coffee run.
    /// </remarks>
    [Fact]
    public async Task A_bank_charging_less_than_the_expense_is_not_a_difference_a_tip_explains()
    {
        await Expense("Coffee run", 17.43m, Dinner);
        var row = await Row(14.99m, on: Dinner.AddDays(1));

        Assert.Empty(await Inbox.Matches(row.Id, Ct));
    }

    /// <summary>
    /// People round when they type, and a round figure above the charge is the one case
    /// where a charge coming in lower still reads as the same money.
    /// </summary>
    [Fact]
    public async Task A_charge_under_a_round_figure_somebody_typed_is_still_possible()
    {
        await Expense("Groceries", 60m, Dinner);
        var row = await Row(58.40m, on: Dinner);

        var match = Assert.Single(await Inbox.Matches(row.Id, Ct));

        Assert.Equal(MatchConfidence.Possible, match.Confidence);
    }

    /// <summary>
    /// A pending authorisation is often taken before the tip is added, so for a pending row
    /// the charge coming in lower is the expected direction rather than the suspicious one.
    /// </summary>
    [Fact]
    public async Task A_pending_row_may_be_lower_than_the_expense_because_the_tip_is_not_on_it_yet()
    {
        await Expense("Dinner", 46.20m, Dinner);
        var row = await Row(40m, on: Dinner, pending: true);

        var match = Assert.Single(await Inbox.Matches(row.Id, Ct));

        Assert.Equal(MatchConfidence.Possible, match.Confidence);
    }

    [Fact]
    public async Task A_posted_row_gets_no_such_allowance()
    {
        await Expense("Dinner", 46.20m, Dinner);
        var row = await Row(40m, on: Dinner);

        Assert.Empty(await Inbox.Matches(row.Id, Ct));
    }

    // ---- the place -----------------------------------------------------------------------

    [Fact]
    public async Task Two_places_that_are_not_the_same_place_rule_out_a_possible_match()
    {
        var coffee = await Place("Blue Bottle Coffee");
        var streaming = await Place("Netflix");

        await Expense("Coffee run", 17.43m, Dinner, at: coffee);
        var row = await Row(20m, on: Dinner, at: streaming);

        Assert.Empty(await Inbox.Matches(row.Id, Ct));
    }

    /// <summary>
    /// One shop can end up as two merchants -- a chain and its subscription arm, a shop and
    /// its parent -- and refusing a real duplicate is the expensive mistake. So the exact
    /// amount wins and the disagreement is overruled.
    /// </summary>
    [Fact]
    public async Task A_place_that_disagrees_does_not_rule_out_a_confident_one()
    {
        await Expense("Prime", 12.99m, Dinner, at: await Place("Amazon"));
        var row = await Row(12.99m, on: Dinner, at: await Place("Amazon Prime"));

        var match = Assert.Single(await Inbox.Matches(row.Id, Ct));

        Assert.Equal(MatchConfidence.Confident, match.Confidence);
    }

    /// <summary>
    /// Most expenses somebody types have no merchant and never will, so one side knowing
    /// where the money went and the other not is silence rather than disagreement.
    /// </summary>
    [Fact]
    public async Task An_expense_that_names_no_place_is_not_disagreeing_with_one_that_does()
    {
        await Expense("Dinner", 40m, Dinner);
        var row = await Row(46m, on: Dinner, at: await Place("Trattoria da Enzo"));

        Assert.Single(await Inbox.Matches(row.Id, Ct));
    }

    // ---- the order -----------------------------------------------------------------------

    [Fact]
    public async Task A_confident_match_is_offered_before_a_possible_one()
    {
        await Expense("Tipped", 40m, Dinner);
        var exact = await Expense("Exactly", 46m, Dinner);
        var row = await Row(46m, on: Dinner);

        var matches = await Inbox.Matches(row.Id, Ct);

        Assert.Equal(2, matches.Count);
        Assert.Equal(exact.Id, matches[0].Expense.Id);
    }

    /// <summary>
    /// The place outranks a nearer amount, because the inbox leads with the best candidate:
    /// ordering by closeness alone let a coincidence a few cents nearer keep the genuine
    /// duplicate off the screen entirely.
    /// </summary>
    [Fact]
    public async Task Agreeing_on_the_place_outranks_being_closer_in_amount()
    {
        var trattoria = await Place("Trattoria da Enzo");

        await Expense("Something else", 45m, Dinner);
        var dinner = await Expense("Dinner", 40m, Dinner, at: trattoria);
        var row = await Row(46m, on: Dinner, at: trattoria);

        var matches = await Inbox.Matches(row.Id, Ct);

        Assert.Equal(2, matches.Count);
        Assert.Equal(dinner.Id, matches[0].Expense.Id);
    }

    // ---- what follows from the grade -----------------------------------------------------

    /// <summary>
    /// The whole reason the guard is not graded: this is the commonest real duplicate there
    /// is, and it is never a confident match.
    /// </summary>
    [Fact]
    public async Task Filing_is_still_refused_over_a_possible_match()
    {
        await Expense("Dinner", 40m, Dinner);
        var row = await Row(46m, on: Dinner.AddDays(2));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Inbox.File(row.Id, new FileBankTransactionRequest(), Ct));

        Assert.Equal(ErrorCodes.PossibleDuplicateExpense, refusal.Code);

        var named = Assert.IsAssignableFrom<IReadOnlyList<ExpenseMatchResponse>>(
            refusal.Extensions[ProblemDetails.MatchesExtension]);

        Assert.Equal(MatchConfidence.Possible, Assert.Single(named).Confidence);
        Assert.Equal(1, await DbContext.Set<Expense>().CountAsync(Ct));
    }

    /// <summary>
    /// A count somebody reads as "some of these are already recorded" has to mean it. A
    /// possible match is a question for whoever files the row, not a claim about it.
    /// </summary>
    [Fact]
    public async Task The_summary_counts_confident_matches_and_not_possible_ones()
    {
        await Expense("Dinner", 40m, Dinner);
        await Row(46m, on: Dinner, providerId: "tipped");

        var possibleOnly = await Inbox.Summary(withDuplicates: true, Ct);

        Assert.Equal(1, possibleOnly.NewCount);
        Assert.Equal(0, possibleOnly.PossibleDuplicates);

        await Expense("Groceries", 82.14m, Dinner);
        await Row(82.14m, on: Dinner, providerId: "exact");

        var withConfident = await Inbox.Summary(withDuplicates: true, Ct);

        Assert.Equal(2, withConfident.NewCount);
        Assert.Equal(1, withConfident.PossibleDuplicates);
    }

    // ---- setup ---------------------------------------------------------------------------

    /// <summary>One shared place money gets spent, as the sync would have made it.</summary>
    private async Task<Merchant> Place(string name)
    {
        var merchant = new Merchant
        {
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            FirstSeenAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(merchant);
        await DbContext.SaveChangesAsync(Ct);

        return merchant;
    }

    /// <summary>
    /// An expense the caller paid, on a day, with no bank row behind it -- and a place only
    /// if somebody said where.
    /// </summary>
    private async Task<Expense> Expense(string name, decimal amount, DateOnly on, Merchant? at = null)
    {
        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = name,
            Amount = amount,
            DateTime = on.ToDateTime(new TimeOnly(20, 30), DateTimeKind.Utc)
        }, Ct);

        if (at is not null)
        {
            expense.MerchantId = at.Id;
            await DbContext.SaveChangesAsync(Ct);
        }

        return expense;
    }

    /// <summary>One imported row on the caller's own connection, waiting in the inbox.</summary>
    private async Task<BankTransaction> Row(decimal amount, DateOnly on, Merchant? at = null,
        bool pending = false, string providerId = "t1")
    {
        var connection = await DbContext.Set<BankConnection>()
            .Include(candidate => candidate.Accounts)
            .FirstOrDefaultAsync(candidate => candidate.UserId == GetService<ICurrentUser>().User.Id, Ct);

        if (connection is null)
        {
            connection = new BankConnection
            {
                User = GetService<ICurrentUser>().User,
                Provider = FakeBankConnector.Name,
                ProviderItemId = $"item-{Guid.NewGuid():N}",
                InstitutionName = "Fake Bank",
                AccessTokenCiphertext = GetService<IAccessTokenProtector>().Protect(FakeBankConnector.AccessToken),
                LinkedAt = DateTimeOffset.UtcNow
            };

            connection.Accounts.Add(new LinkedAccount
            {
                ProviderAccountId = "acc-1",
                Name = "Everyday",
                Type = "depository"
            });

            DbContext.Add(connection);
            await DbContext.SaveChangesAsync(Ct);
        }

        var row = new BankTransaction
        {
            Account = connection.Accounts.First(),
            ProviderTransactionId = providerId,
            Date = on,
            Amount = amount,
            Currency = "USD",
            Description = "CARD PURCHASE",
            MerchantName = at?.Name,
            MerchantId = at?.Id,
            Pending = pending,
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(row);
        await DbContext.SaveChangesAsync(Ct);

        return row;
    }
}
