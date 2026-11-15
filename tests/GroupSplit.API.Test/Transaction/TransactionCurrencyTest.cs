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
    [Theory]
    [InlineData("JPY", "10.5")]
    [InlineData("USD", "10.001")]
    [InlineData("KWD", "10.0001")]
    public async Task Amounts_must_use_their_currencies_minor_units(string currency, string text)
    {
        var amount = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        await Assert.ThrowsAsync<GroupSplit.API.Errors.ValidationException>(() => Personal(amount, currency));
    }

    [Fact]
    public async Task Three_decimal_currency_is_preserved()
    {
        var expense = await Personal(10.001m, "KWD");
        Assert.Equal(10.001m, expense.Amount);
        Assert.Equal("KWD", expense.Currency);
    }

    [Fact]
    public async Task Editing_currency_changes_the_expense_without_converting_the_amount()
    {
        var expense = await Personal(10m, "USD");
        var update = (await Transactions.GetUpdateModel(expense.Id, Ct))!;
        update.Currency = "EUR";
        await Transactions.Update(expense.Id, update, Ct);
        var saved = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == expense.Id, Ct);
        Assert.Equal("EUR", saved.Currency);
        Assert.Equal(10m, saved.Amount);
    }

    [Theory]
    [InlineData("ZZZ")]
    [InlineData("JPY")]
    public async Task Editing_rejects_unsupported_currency_or_incompatible_precision(string currency)
    {
        var expense = await Personal(10.25m, "USD");
        var update = (await Transactions.GetUpdateModel(expense.Id, Ct))!;
        update.Currency = currency;
        await Assert.ThrowsAsync<GroupSplit.API.Errors.ValidationException>(() => Transactions.Update(expense.Id, update, Ct).AsTask());
    }

    [Fact]
    public async Task Changing_currency_updates_the_attached_receipt()
    {
        var expense = await Personal(10m, "USD");
        var receipt = new Receipt { ExpenseId = expense.Id, Currency = "USD", Subtotal = 10m, Total = 10m };
        DbContext.Set<Receipt>().Add(receipt);
        await DbContext.SaveChangesAsync(Ct);
        var update = (await Transactions.GetUpdateModel(expense.Id, Ct))!;
        update.Currency = "EUR";
        await Transactions.Update(expense.Id, update, Ct);
        Assert.Equal("EUR", (await DbContext.Set<Receipt>().AsNoTracking().SingleAsync(r => r.Id == receipt.Id, Ct)).Currency);
    }

    [Fact]
    public async Task Currency_change_rejects_receipt_amounts_with_incompatible_precision()
    {
        var expense = await Personal(10m, "USD");
        DbContext.Set<Receipt>().Add(new Receipt { ExpenseId = expense.Id, Currency = "USD", Subtotal = 9.5m, Tip = .5m, Total = 10m });
        await DbContext.SaveChangesAsync(Ct);
        var update = (await Transactions.GetUpdateModel(expense.Id, Ct))!;
        update.Currency = "JPY";
        await Assert.ThrowsAsync<GroupSplit.API.Errors.ValidationException>(() => Transactions.Update(expense.Id, update, Ct).AsTask());
    }
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

    [Fact]
    public async Task Changing_the_group_default_never_relabels_existing_expenses()
    {
        var groupId = await AGroup();
        var old = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Before",
            Amount = 10m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId
        }, Ct);
        var groups = GetService<IGroupService>();
        var update = (await groups.GetUpdateModel(groupId, Ct))!;
        update.Currency = "EUR";
        await groups.UpdateGroup(groupId, update, Ct);
        var newer = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "After",
            Amount = 10m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId
        }, Ct);
        Assert.Equal("USD", old.Currency);
        Assert.Equal("EUR", newer.Currency);
        Assert.Equal("USD", (await DbContext.Set<Expense>().AsNoTracking().SingleAsync(expense => expense.Id == old.Id, Ct)).Currency);
    }

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
    public async Task A_group_expense_in_another_currency_keeps_its_currency()
    {
        var group = await AGroup();

        var expense = await Transactions.Create(
            new CreateTransactionRequest
            {
                Name = "Rent",
                Amount = 900m,
                Currency = "EUR",
                DateTime = DateTimeOffset.UtcNow,
                GroupId = group
            }, Ct);
        Assert.Equal("EUR", expense.Currency);
        Assert.Equal(900m, expense.Amount);
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
    public async Task The_preview_accepts_a_currency_other_than_the_groups_default()
    {
        var group = await AGroup();

        var preview = await Transactions.Preview(
            new CreateTransactionRequest
            {
                Name = "Rent",
                Amount = 900m,
                Currency = "EUR",
                DateTime = DateTimeOffset.UtcNow,
                GroupId = group
            }, Ct);
        Assert.NotNull(preview);
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
