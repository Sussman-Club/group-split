using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Looking further back for the expense an imported bank row already is, when none of the
/// suggested ones is it (#326).
/// </summary>
/// <remarks>
/// It writes nothing itself: it hands the chosen expense back to the filing dialog, which
/// attaches the row to it. So what matters is which expense comes back, and that one the
/// API would refuse -- already carrying a bank row, or in another currency -- cannot be
/// handed back at all.
/// </remarks>
public class FindExpenseDialogTest : ComponentTest
{
    private static readonly BankTransactionResponse Row = new(
        Guid.NewGuid(), new DateOnly(2026, 10, 4), 42.50m, "USD", "WHOLEFDS 1234", "Whole Foods", null, null,
        null, null, null, null, null, Pending: false, InboxStatus.New, TransactionId: null, RemovedAt: null,
        "Everyday", "Test Bank");

    private static readonly TransactionResponse Groceries = Expense("Groceries", 42.50m, "usd");
    private static readonly TransactionResponse Fuel = Expense("Fuel", 42.50m, "USD") with { HasBankTransaction = true };
    private static readonly TransactionResponse Museum = Expense("Museum", 42.50m, "EUR");

    private readonly Mock<ITransactionsClient> _transactions = new();

    public FindExpenseDialogTest()
    {
        Answer([Groceries, Fuel, Museum]);

        Services.AddSingleton(_transactions.Object);
    }

    private static TransactionResponse Expense(string name, decimal amount, string currency) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Amount = amount,
        Currency = currency,
        DateTime = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
        GroupName = "Flat",
        PaidByUserName = "Ana"
    };

    private void Answer(TransactionResponse[] expenses) =>
        _transactions
            .Setup(client => client.GetTransactionsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfTransactionResponse(expenses, 1, 20, expenses.Length));

    private void Searched(string? search, Times times) =>
        _transactions.Verify(client => client.GetTransactionsAsync(null, null, null, null, null, null, search,
            "dateTime", true, 1, 20, It.IsAny<CancellationToken>()), times);

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> OpenAsync()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () =>
            dialog = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<FindExpenseDialog>("Find another expense", new DialogParameters<FindExpenseDialog>
                {
                    { d => d.Row, Row }
                }));

        return (provider, dialog);
    }

    private static IElement Choice(IRenderedComponent<MudDialogProvider> provider, string name) =>
        provider.FindAll(".gs-match-choice")
            .Single(choice => choice.QuerySelector(".title")!.TextContent.Trim() == name);

    private static Task SearchForAsync(IRenderedComponent<MudDialogProvider> provider, string text)
    {
        var field = provider.FindComponents<MudTextField<string>>()
            .Single(candidate => candidate.Instance.Label == "Search expenses");

        return provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(text));
    }

    [Fact]
    public async Task It_opens_on_the_most_recent_expenses_with_their_amounts()
    {
        var (provider, _) = await OpenAsync();

        Searched(null, Times.Once());
        Assert.Contains("Recent expenses", provider.Markup, StringComparison.Ordinal);

        Assert.Equal(["Groceries", "Fuel", "Museum"],
            provider.FindAll(".gs-match-choice .title").Select(title => title.TextContent.Trim()));
        Assert.Equal("$42.50", Choice(provider, "Groceries").QuerySelector(".gs-money")!.TextContent);
        Assert.Equal("€42.50", Choice(provider, "Museum").QuerySelector(".gs-money")!.TextContent);
    }

    [Fact]
    public async Task Choosing_an_expense_hands_that_expense_back()
    {
        // "usd" against the row's "USD": the API does not care about the case, so neither
        // does the dialog.
        var (provider, dialog) = await OpenAsync();

        await Choice(provider, "Groceries").ClickAsync(new MouseEventArgs());

        var chosen = await dialog.GetReturnValueAsync<TransactionResponse>();

        Assert.Equal(Groceries.Id, chosen!.Id);
        Assert.Equal(42.50m, chosen.Amount);
    }

    [Fact]
    public async Task An_expense_the_api_would_refuse_cannot_be_chosen()
    {
        var (provider, dialog) = await OpenAsync();

        var fuel = Choice(provider, "Fuel");
        var museum = Choice(provider, "Museum");

        Assert.True(fuel.HasAttribute("disabled"));
        Assert.Contains("Already linked to a bank transaction", fuel.TextContent, StringComparison.Ordinal);
        Assert.True(museum.HasAttribute("disabled"));
        Assert.Contains("Different currency", museum.TextContent, StringComparison.Ordinal);
        Assert.False(Choice(provider, "Groceries").HasAttribute("disabled"));

        // A disabled attribute is the browser's to honour; the handler checks again.
        await fuel.ClickAsync(new MouseEventArgs());
        await Choice(provider, "Museum").ClickAsync(new MouseEventArgs());

        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task Searching_asks_for_the_trimmed_text()
    {
        var (provider, _) = await OpenAsync();

        Answer([Groceries]);
        await SearchForAsync(provider, "  groceries ");
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Search")
            .ClickAsync(new MouseEventArgs());

        Searched("groceries", Times.Once());
        Assert.Contains("Matching expenses", provider.Markup, StringComparison.Ordinal);
        Assert.Equal(["Groceries"], provider.FindAll(".gs-match-choice .title").Select(title => title.TextContent.Trim()));
    }

    [Fact]
    public async Task Cancel_hands_nothing_back()
    {
        var (provider, dialog) = await OpenAsync();

        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel")
            .ClickAsync(new MouseEventArgs());

        var result = await dialog.Result;

        Assert.True(result!.Canceled);
    }

    [Fact]
    public async Task A_search_that_fails_says_so_and_leaves_nothing_stale_to_choose()
    {
        var (provider, dialog) = await OpenAsync();
        Assert.NotEmpty(provider.FindAll(".gs-match-choice"));

        // What was listed before belongs to the last search; offering it under a failed
        // one invites attaching the row to an expense nobody looked for.
        _transactions
            .Setup(client => client.GetTransactionsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("The server could not be reached."));

        await SearchForAsync(provider, "dinner");
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Search")
            .ClickAsync(new MouseEventArgs());

        Assert.Empty(provider.FindAll(".gs-match-choice"));
        Assert.Contains("No expenses found.", provider.Markup, StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);

        Snackbar.Verify(snackbar => snackbar.Add(
            It.Is<string>(message => message.StartsWith("Could not search expenses.", StringComparison.Ordinal)),
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);

        // And the button is back for another try.
        Assert.False(provider.FindAll("button").Single(button => button.TextContent.Trim() == "Search")
            .HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_first_load_that_fails_offers_nothing_and_stays_open()
    {
        _transactions
            .Setup(client => client.GetTransactionsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("The server could not be reached."));

        var (provider, dialog) = await OpenAsync();

        Assert.Empty(provider.FindAll(".gs-match-choice"));
        Assert.Contains("No expenses found.", provider.Markup, StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);

        Snackbar.Verify(snackbar => snackbar.Add(
            It.Is<string>(message => message.StartsWith("Could not search expenses.", StringComparison.Ordinal)),
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);
    }
}
