using GroupSplit.API.Endpoints;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// What an expense is in, and what the totals do when there is more than one answer.
/// </summary>
/// <remarks>
/// A group keeps its balances in one currency and conversion is out of scope. A personal
/// expense may be in any (#320): it was stamped with the default whatever it was given, so
/// a 20.00 EUR expense was written down as 20.00 USD. Once two currencies can sit in one
/// listing, a single sum over it is a number that is not an amount of anything, so the
/// totals keep them apart.
/// </remarks>
public class TransactionCurrencyTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ITransactionService Transactions => GetService<ITransactionService>();

    private Task<Expense> Personal(decimal amount, string? currency, DateTimeOffset? when = null) =>
        Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = amount,
            Currency = currency,
            DateTime = when ?? DateTimeOffset.UtcNow
        }, Ct).AsTask();

    private async Task<Guid> AGroup(string currency = Currencies.Default)
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        if (currency != Currencies.Default)
        {
            DbContext.Entry(group).Property(g => g.Currency).CurrentValue = currency;
            await DbContext.SaveChangesAsync(Ct);
        }

        return group.Id;
    }

    // ---- What an expense is recorded in ------------------------------------------------

    [Fact]
    public async Task A_personal_expense_keeps_the_currency_it_was_created_with()
    {
        var expense = await Personal(20m, "EUR");

        var stored = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == expense.Id, Ct);

        Assert.Equal("EUR", stored.Currency);
        Assert.Equal(20m, stored.Amount);
    }

    [Fact]
    public async Task A_currency_is_stored_as_its_code_whatever_case_it_was_sent_in()
    {
        var expense = await Personal(20m, " gbp ");

        Assert.Equal("GBP", expense.Currency);
    }

    [Fact]
    public async Task A_personal_expense_that_names_no_currency_is_in_the_default()
    {
        var expense = await Personal(20m, currency: null);

        Assert.Equal(Currencies.Default, expense.Currency);
    }

    [Fact]
    public async Task A_group_expense_that_names_no_currency_is_in_the_groups()
    {
        var group = await AGroup("EUR");

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Rent",
            Amount = 900m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group
        }, Ct);

        Assert.Equal("EUR", expense.Currency);
    }

    [Fact]
    public async Task A_group_expense_in_another_currency_is_refused_rather_than_relabelled()
    {
        var group = await AGroup();

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Transactions.Create(
            new CreateTransactionRequest
            {
                Name = "Rent",
                Amount = 900m,
                Currency = "EUR",
                DateTime = DateTimeOffset.UtcNow,
                GroupId = group
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.CurrencyMismatch, refusal.Code);
        Assert.Equal("EUR", refusal.Extensions["transactionCurrency"]);
        Assert.Equal(Currencies.Default, refusal.Extensions["groupCurrency"]);
        Assert.Empty(await DbContext.Set<Expense>().ToListAsync(Ct));
    }

    [Fact]
    public async Task Naming_the_groups_own_currency_is_the_same_as_naming_none()
    {
        var group = await AGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Rent",
            Amount = 900m,
            Currency = "usd",
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group
        }, Ct);

        Assert.Equal(Currencies.Default, expense.Currency);
    }

    [Fact]
    public async Task The_preview_refuses_a_currency_the_save_would_refuse()
    {
        var group = await AGroup();

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Transactions.Preview(
            new CreateTransactionRequest
            {
                Name = "Rent",
                Amount = 900m,
                Currency = "EUR",
                DateTime = DateTimeOffset.UtcNow,
                GroupId = group
            }, Ct));

        Assert.Equal(ErrorCodes.CurrencyMismatch, refusal.Code);
    }

    [Fact]
    public async Task The_listing_says_which_currency_each_expense_is_in()
    {
        await Personal(20m, "EUR");

        var page = await (await Transactions.List(Ct)).ToTransactionPageAsync(
            new TransactionFilter(), new SortRequest(), new PageRequest(), Ct);

        Assert.Equal("EUR", Assert.Single(page.Items).Currency);
    }

    // ---- What the totals do with more than one -------------------------------------------

    [Fact]
    public async Task The_paid_total_never_adds_one_currency_to_another()
    {
        await Personal(10m, null);
        await Personal(15m, null);
        await Personal(20m, "EUR");
        await Personal(5m, "GBP");
        await Personal(7m, "EUR");

        var summary = await (await Transactions.List(Ct)).ToSummaryAsync(new TransactionFilter(), Ct);

        Assert.Equal(5, summary.Count);
        Assert.Equal(Currencies.Default, summary.Currency);
        Assert.Equal(25m, summary.Total);

        Assert.Collection(summary.OtherCurrencies,
            eur => Assert.Equal(new CurrencyTotalResponse("EUR", 2, 27m), eur),
            gbp => Assert.Equal(new CurrencyTotalResponse("GBP", 1, 5m), gbp));
    }

    [Fact]
    public async Task A_match_with_nothing_in_the_default_totals_in_the_currency_it_is_in()
    {
        await Personal(20m, "EUR");
        await Personal(7m, "EUR");
        await Personal(5m, "GBP");

        var summary = await (await Transactions.List(Ct)).ToSummaryAsync(new TransactionFilter(), Ct);

        Assert.Equal("EUR", summary.Currency);
        Assert.Equal(27m, summary.Total);
        Assert.Equal(new CurrencyTotalResponse("GBP", 1, 5m), Assert.Single(summary.OtherCurrencies));
    }

    [Fact]
    public async Task One_currency_is_the_ordinary_summary_with_nothing_beside_it()
    {
        await Personal(10m, null);

        var summary = await (await Transactions.List(Ct)).ToSummaryAsync(new TransactionFilter(), Ct);

        Assert.Equal(Currencies.Default, summary.Currency);
        Assert.Empty(summary.OtherCurrencies);
    }

    [Fact]
    public async Task The_share_total_keeps_currencies_apart_as_well()
    {
        await Personal(10m, null);
        await Personal(20m, "EUR");

        var me = GetService<ICurrentUser>().User.Id;
        var summary = await (await Transactions.Shares(Ct)).ToShareSummaryAsync(
            await Transactions.List(Ct), new TransactionFilter(), me, owedOnly: false, Ct);

        Assert.Equal(2, summary.Count);
        Assert.Equal(Currencies.Default, summary.Currency);
        Assert.Equal(10m, summary.Total);
        Assert.Equal(10m, summary.Share);
        Assert.Equal(0m, summary.OwedToOthers);

        Assert.Equal(new CurrencyShareTotalResponse("EUR", 1, 20m, 20m, 0m), Assert.Single(summary.OtherCurrencies));
    }

    [Fact]
    public async Task A_share_row_says_which_currency_it_is_in()
    {
        await Personal(20m, "EUR");

        var me = GetService<ICurrentUser>().User.Id;
        var page = await (await Transactions.Shares(Ct)).ToSharePageAsync(
            await Transactions.List(Ct), new TransactionFilter(), new SortRequest(), new PageRequest(),
            me, owedOnly: false, Ct);

        Assert.Equal("EUR", Assert.Single(page.Items).Currency);
    }

    [Fact]
    public void The_headline_currency_is_the_default_wherever_anything_is_in_it()
    {
        Assert.Equal(Currencies.Default, TransactionApi.HeadlineCurrency([]));
        Assert.Equal(Currencies.Default, TransactionApi.HeadlineCurrency([("EUR", 9), (Currencies.Default, 1)]));
        Assert.Equal("EUR", TransactionApi.HeadlineCurrency([("GBP", 1), ("EUR", 2)]));
        // A tie goes to the alphabet, so the same rows always answer the same way.
        Assert.Equal("EUR", TransactionApi.HeadlineCurrency([("GBP", 2), ("EUR", 2)]));
    }
}
