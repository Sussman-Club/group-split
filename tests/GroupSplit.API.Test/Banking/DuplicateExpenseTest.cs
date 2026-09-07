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
/// The same dinner arriving twice: once typed at the table, once from the card two days
/// later. What has to be true is that the second one cannot quietly become a second expense
/// -- so this pins the refusal, both answers to it, and the shape of the window that decides
/// whether there is anything to ask about.
/// </summary>
public class DuplicateExpenseTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();

    /// <summary>The day the dinner was eaten. Everything here is relative to it.</summary>
    private static readonly DateOnly Dinner = new(2026, 9, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInboxService Inbox => GetService<IInboxService>();

    private IDuplicateMatcher Matcher => GetService<IDuplicateMatcher>();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);

    // ---- the refusal ---------------------------------------------------------------------

    [Fact]
    public async Task Filing_a_row_that_looks_like_an_expense_already_recorded_is_refused_and_names_it()
    {
        var typed = await Expense("Dinner", 40m, Dinner);
        var row = await Row(46m, on: Dinner.AddDays(2), merchant: "SQ *TRATTORIA 4421");

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Inbox.File(row.Id, new FileBankTransactionRequest(), Ct));

        Assert.Equal(ErrorCodes.PossibleDuplicateExpense, refusal.Code);

        var named = Assert.IsAssignableFrom<IReadOnlyList<ExpenseMatchResponse>>(refusal.Extensions["matches"]);
        var match = Assert.Single(named);

        Assert.Equal(typed.Id, match.TransactionId);
        Assert.Equal("Dinner", match.Name);
        Assert.Equal(6m, match.AmountDifference);
        Assert.Equal(2, match.DaysApart);

        // The whole point: the second expense does not exist by the time anybody is told.
        Assert.Equal(1, await DbContext.Set<Expense>().CountAsync(Ct));
        Assert.Equal(BankTransactionStatus.New, (await Reload(row)).Status);
    }

    [Fact]
    public async Task Filing_it_anyway_records_the_second_expense_because_they_really_did_pay_twice()
    {
        await Expense("Coffee", 4m, Dinner);
        var row = await Row(4m, on: Dinner);

        var second = await Inbox.File(row.Id, new FileBankTransactionRequest { FileAnyway = true }, Ct);

        Assert.Equal(2, await DbContext.Set<Expense>().CountAsync(Ct));
        Assert.Equal(row.Id, second.BankTransactionId);
        Assert.Equal(BankTransactionStatus.Filed, (await Reload(row)).Status);
    }

    [Fact]
    public async Task Nothing_is_raised_when_there_is_no_expense_it_could_be()
    {
        await Expense("Dinner", 40m, Dinner);
        var row = await Row(180m, on: Dinner);

        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        Assert.Equal(180m, expense.Amount);
        Assert.Equal(2, await DbContext.Set<Expense>().CountAsync(Ct));
    }

    // ---- pointing it at the expense that is already there ---------------------------------

    [Fact]
    public async Task Attaching_the_row_to_the_expense_leaves_one_expense_with_the_bank_row_on_it()
    {
        var typed = await Expense("Dinner", 40m, Dinner);
        var row = await Row(46m, on: Dinner.AddDays(2));

        var linked = await Inbox.Link(row.Id, new LinkBankTransactionRequest { TransactionId = typed.Id }, Ct);

        Assert.Equal(typed.Id, linked.Id);
        Assert.Equal(1, await DbContext.Set<Expense>().CountAsync(Ct));

        var stored = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(Ct);

        Assert.Equal(row.Id, stored.BankTransactionId);
        Assert.Equal(BankTransactionStatus.Filed, (await Reload(row)).Status);

        // What the person wrote down stays what the person wrote down. The card settling
        // for six more is not a correction anybody asked for.
        Assert.Equal(40m, stored.Amount);
        Assert.Equal(Dinner, DateOnly.FromDateTime(stored.DateTime.UtcDateTime));
    }

    [Fact]
    public async Task An_attached_row_is_not_suggested_against_anything_else()
    {
        var typed = await Expense("Dinner", 40m, Dinner);
        var row = await Row(40m, on: Dinner);

        await Inbox.Link(row.Id, new LinkBankTransactionRequest { TransactionId = typed.Id }, Ct);

        Assert.Empty(await Inbox.Matches(row.Id, Ct));
    }

    [Fact]
    public async Task An_expense_that_already_came_from_a_bank_row_cannot_take_a_second_one()
    {
        var first = await Row(40m, on: Dinner, providerId: "t1");
        var filed = await Inbox.File(first.Id, new FileBankTransactionRequest(), Ct);
        var second = await Row(40m, on: Dinner, providerId: "t2");

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Inbox.Link(second.Id, new LinkBankTransactionRequest { TransactionId = filed.Id }, Ct));

        Assert.Equal(ErrorCodes.TransactionAlreadyImported, refusal.Code);
    }

    [Fact]
    public async Task Money_coming_in_is_not_attached_to_an_expense_either()
    {
        var typed = await Expense("Dinner", 40m, Dinner);
        var refund = await Row(-40m, on: Dinner);

        var refusal = await Assert.ThrowsAsync<UnprocessableException>(() =>
            Inbox.Link(refund.Id, new LinkBankTransactionRequest { TransactionId = typed.Id }, Ct));

        Assert.Equal(ErrorCodes.BankTransactionIsCredit, refusal.Code);
    }

    [Fact]
    public async Task Somebody_elses_expense_is_not_found_rather_than_forbidden()
    {
        var theirs = await TestDataUtils.CreateTransactionForNewUserAsync(ServiceProvider, "Theirs", 40m);
        var row = await Row(40m, on: Dinner);

        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            Inbox.Link(row.Id, new LinkBankTransactionRequest { TransactionId = theirs.Id }, Ct));

        Assert.Equal(ErrorCodes.TransactionNotFound, refusal.Code);
    }

    // ---- an answer that was given stays given --------------------------------------------

    [Fact]
    public async Task A_dismissed_pair_is_not_raised_again_and_filing_then_goes_through()
    {
        var typed = await Expense("Dinner", 40m, Dinner);
        var row = await Row(40m, on: Dinner);

        Assert.NotEmpty(await Inbox.Matches(row.Id, Ct));

        await Inbox.DismissMatch(row.Id, new DismissBankMatchRequest { TransactionId = typed.Id }, Ct);

        Assert.Empty(await Inbox.Matches(row.Id, Ct));

        // And nothing is standing in the way of filing it any more, because there is no
        // longer anything to be told about.
        await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        Assert.Equal(2, await DbContext.Set<Expense>().CountAsync(Ct));
    }

    [Fact]
    public async Task Dismissing_one_pair_leaves_the_other_suggestions_standing()
    {
        var dismissed = await Expense("Dinner", 40m, Dinner);
        var other = await Expense("Supper", 41m, Dinner);
        var row = await Row(40m, on: Dinner);

        await Inbox.DismissMatch(row.Id, new DismissBankMatchRequest { TransactionId = dismissed.Id }, Ct);

        var still = Assert.Single(await Inbox.Matches(row.Id, Ct));

        Assert.Equal(other.Id, still.Expense.Id);
    }

    [Fact]
    public async Task Dismissing_the_same_pair_twice_is_the_same_answer_and_not_a_second_one()
    {
        var typed = await Expense("Dinner", 40m, Dinner);
        var row = await Row(40m, on: Dinner);
        var request = new DismissBankMatchRequest { TransactionId = typed.Id };

        await Inbox.DismissMatch(row.Id, request, Ct);
        await Inbox.DismissMatch(row.Id, request, Ct);

        Assert.Equal(1, await DbContext.Set<BankMatchDismissal>().CountAsync(Ct));
    }

    // ---- the other order -----------------------------------------------------------------

    [Fact]
    public async Task Recording_an_expense_that_matches_a_row_waiting_in_the_inbox_is_caught_too()
    {
        var row = await Row(46m, on: Dinner);
        var typed = await Expense("Dinner", 40m, Dinner.AddDays(1));

        var waiting = Assert.Single(await Matcher.RowsLike(typed, Ct));

        Assert.Equal(row.Id, waiting.Id);
    }

    [Fact]
    public async Task A_row_already_dealt_with_is_not_offered_against_a_new_expense()
    {
        var row = await Row(40m, on: Dinner);
        await Inbox.Ignore(row.Id, Ct);

        var typed = await Expense("Dinner", 40m, Dinner);

        Assert.Empty(await Matcher.RowsLike(typed, Ct));
    }

    [Fact]
    public async Task An_expense_that_already_carries_a_bank_row_is_not_asked_about_again()
    {
        var row = await Row(40m, on: Dinner, providerId: "t1");
        var filed = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        await Row(40m, on: Dinner, providerId: "t2");

        Assert.Empty(await Matcher.RowsLike(filed, Ct));
    }

    // ---- what the window and the tolerance actually allow ---------------------------------

    [Theory]
    // A tip added after the receipt, and a charge that posts two days later: the case the
    // whole feature exists for.
    [InlineData(40, 46, 2, true)]
    // Same money, same day.
    [InlineData(40, 40, 0, true)]
    // Small sums get a flat tolerance rather than one measured in pennies.
    [InlineData(4.00, 4.75, 0, true)]
    // The far edge of the window, and one day past it.
    [InlineData(40, 40, 5, true)]
    [InlineData(40, 40, 6, false)]
    // Not the same money at all.
    [InlineData(40, 80, 0, false)]
    [InlineData(40, 55, 0, false)]
    public async Task The_window_says_what_could_be_the_same_money(
        decimal typedAmount, decimal chargedAmount, int daysLater, bool suggested)
    {
        await Expense("Dinner", typedAmount, Dinner);
        var row = await Row(chargedAmount, on: Dinner.AddDays(daysLater));

        var matches = await Inbox.Matches(row.Id, Ct);

        Assert.Equal(suggested, matches.Count > 0);
    }

    [Fact]
    public async Task Money_in_another_currency_is_not_the_same_money()
    {
        await Expense("Dinner", 40m, Dinner);
        var row = await Row(40m, on: Dinner, currency: "EUR");

        Assert.Empty(await Inbox.Matches(row.Id, Ct));
    }

    [Fact]
    public async Task An_expense_somebody_else_paid_is_never_a_candidate()
    {
        await TestDataUtils.CreateTransactionForNewUserAsync(ServiceProvider, "Theirs", 40m);
        var row = await Row(40m, on: DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.Empty(await Inbox.Matches(row.Id, Ct));
    }

    [Fact]
    public async Task The_bank_line_reading_nothing_like_the_expense_is_no_obstacle()
    {
        // "Dinner" against SQ *TRATTORIA 4421 is the ordinary case, not the exception, so a
        // name that does not line up must not stop a match.
        var typed = await Expense("Dinner", 40m, Dinner);
        var row = await Row(40m, on: Dinner, merchant: "SQ *TRATTORIA 4421", description: "SQ *TRATTORIA 4421");

        var match = Assert.Single(await Inbox.Matches(row.Id, Ct));

        Assert.Equal(typed.Id, match.Expense.Id);
    }

    [Fact]
    public async Task The_closest_candidate_is_offered_first()
    {
        await Expense("Nearly", 34m, Dinner);
        var closest = await Expense("Exactly", 40m, Dinner);
        var row = await Row(40m, on: Dinner);

        var matches = await Inbox.Matches(row.Id, Ct);

        Assert.Equal(closest.Id, matches[0].Expense.Id);
    }

    [Fact]
    public async Task A_row_another_person_owns_has_no_matches_to_show_a_stranger()
    {
        await Expense("Dinner", 40m, Dinner);
        var row = await Row(40m, on: Dinner);

        using var scope = ServiceProvider.CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);
        var stranger = scope.ServiceProvider.GetRequiredService<IInboxService>();

        var refusal = await Assert.ThrowsAsync<NotFoundException>(() => stranger.Matches(row.Id, Ct));

        Assert.Equal(ErrorCodes.BankTransactionNotFound, refusal.Code);
    }

    // ---- setup ---------------------------------------------------------------------------

    /// <summary>An expense the caller paid, on a day, with no bank row behind it.</summary>
    private Task<Expense> Expense(string name, decimal amount, DateOnly on) =>
        GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = name,
            Amount = amount,
            DateTime = on.ToDateTime(new TimeOnly(20, 30), DateTimeKind.Utc)
        }, Ct).AsTask();

    /// <summary>One imported row on the caller's own connection, waiting in the inbox.</summary>
    private async Task<BankTransaction> Row(decimal amount, DateOnly on, string? merchant = "Lidl",
        string description = "LIDL 1234", string currency = "USD", string providerId = "t1")
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
            Currency = currency,
            Description = description,
            MerchantName = merchant,
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(row);
        await DbContext.SaveChangesAsync(Ct);

        return row;
    }

    private Task<BankTransaction> Reload(BankTransaction row) =>
        DbContext.Set<BankTransaction>().AsNoTracking().SingleAsync(candidate => candidate.Id == row.Id, Ct);
}
