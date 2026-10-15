using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The question asked right after an expense is saved, when the bank has already sent a row
/// that could be the same money (#326).
/// </summary>
/// <remarks>
/// Both answers write. Attaching points the row at the expense, so one payment stays one
/// expense; "Not the same" is remembered so the pair is not raised again. Getting the ids
/// crossed attaches somebody's card charge to the wrong dinner, so these follow each click
/// through the real <see cref="BankCommands"/> to the request the inbox client is handed.
/// </remarks>
public class AttachBankRowDialogTest : ComponentTest
{
    private static readonly TransactionResponse Dinner = new()
    {
        Id = Guid.NewGuid(),
        Name = "Dinner",
        Amount = 64.20m,
        Currency = "USD",
        DateTime = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero)
    };

    private static readonly BankTransactionResponse Trattoria = Row("Trattoria Roma", 64.20m);
    private static readonly BankTransactionResponse Pizzeria = Row("Pizzeria Napoli", 61.80m);

    private readonly Mock<IInboxClient> _inbox = new();

    public AttachBankRowDialogTest()
    {
        _inbox
            .Setup(client => client.LinkBankTransactionAsync(It.IsAny<Guid>(),
                It.IsAny<LinkBankTransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Dinner with { HasBankTransaction = true });

        Services.AddSingleton(_inbox.Object);
        Services.AddSingleton(Mock.Of<IBankConnectionsClient>());
        Services.AddSingleton<IBankCommands, BankCommands>();
    }

    private static BankTransactionResponse Row(string merchant, decimal amount) => new(
        Guid.NewGuid(), new DateOnly(2026, 10, 4), amount, "USD", merchant.ToUpperInvariant(), merchant, null,
        null, new DateOnly(2026, 10, 3), null, null, null, null, Pending: false, InboxStatus.New,
        TransactionId: null, RemovedAt: null, "Everyday", "Test Bank");

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> OpenAsync()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () =>
            dialog = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<AttachBankRowDialog>("Your bank already sent this",
                    new DialogParameters<AttachBankRowDialog>
                    {
                        { d => d.Expense, Dinner },
                        { d => d.Rows, new[] { Trattoria, Pizzeria } }
                    }));

        return (provider, dialog);
    }

    private static IElement RowOf(IRenderedComponent<MudDialogProvider> provider, string title) =>
        provider.FindAll(".gs-row").Single(row => row.QuerySelector(".title")!.TextContent.Trim() == title);

    private static IElement Button(IElement row, string text) =>
        row.QuerySelectorAll("button").Single(button => button.TextContent.Trim() == text);

    private static string[] Titles(IRenderedComponent<MudDialogProvider> provider) =>
        [.. provider.FindAll(".gs-row .title").Select(title => title.TextContent.Trim())];

    [Fact]
    public async Task Each_row_is_offered_with_the_amount_the_bank_sent()
    {
        var (provider, _) = await OpenAsync();

        Assert.Equal("Dinner", provider.Find(".mud-dialog-content strong").TextContent);
        Assert.Equal(["Trattoria Roma", "Pizzeria Napoli"], Titles(provider));
        Assert.Equal("$64.20", RowOf(provider, "Trattoria Roma").QuerySelector(".gs-money")!.TextContent);
        Assert.Equal("$61.80", RowOf(provider, "Pizzeria Napoli").QuerySelector(".gs-money")!.TextContent);
    }

    [Fact]
    public async Task Attaching_a_row_points_that_row_at_the_expense_and_closes()
    {
        var (provider, dialog) = await OpenAsync();

        await Button(RowOf(provider, "Pizzeria Napoli"), "Attach").ClickAsync(new MouseEventArgs());

        var result = await dialog.Result;
        Assert.False(result!.Canceled);

        _inbox.Verify(client => client.LinkBankTransactionAsync(Pizzeria.Id,
            It.Is<LinkBankTransactionRequest>(request => request.TransactionId == Dinner.Id),
            It.IsAny<CancellationToken>()), Times.Once);
        _inbox.Verify(client => client.LinkBankTransactionAsync(Trattoria.Id,
            It.IsAny<LinkBankTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        Snackbar.Verify(snackbar => snackbar.Add("Attached to Dinner. It is still one expense.", Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Not_the_same_strikes_that_row_off_and_keeps_asking_about_the_rest()
    {
        var (provider, dialog) = await OpenAsync();

        await Button(RowOf(provider, "Trattoria Roma"), "Not the same").ClickAsync(new MouseEventArgs());

        _inbox.Verify(client => client.DismissBankTransactionMatchAsync(Trattoria.Id,
            It.Is<DismissBankMatchRequest>(request => request.TransactionId == Dinner.Id),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(["Pizzeria Napoli"], Titles(provider));
        Assert.False(dialog.Result.IsCompleted);

        // The last open question answered, there is nothing left to ask.
        await Button(RowOf(provider, "Pizzeria Napoli"), "Not the same").ClickAsync(new MouseEventArgs());

        var result = await dialog.Result;
        Assert.False(result!.Canceled);

        _inbox.Verify(client => client.DismissBankTransactionMatchAsync(Pizzeria.Id,
            It.Is<DismissBankMatchRequest>(request => request.TransactionId == Dinner.Id),
            It.IsAny<CancellationToken>()), Times.Once);
        _inbox.Verify(client => client.LinkBankTransactionAsync(It.IsAny<Guid>(),
            It.IsAny<LinkBankTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Deciding_later_writes_nothing()
    {
        var (provider, dialog) = await OpenAsync();

        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Decide later")
            .ClickAsync(new MouseEventArgs());

        await dialog.Result;

        Assert.Empty(_inbox.Invocations);
    }

    [Fact]
    public async Task A_refused_attach_says_why_and_leaves_both_answers_open()
    {
        _inbox
            .Setup(client => client.LinkBankTransactionAsync(It.IsAny<Guid>(),
                It.IsAny<LinkBankTransactionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.TransactionAlreadyImported));

        var (provider, dialog) = await OpenAsync();

        await Button(RowOf(provider, "Trattoria Roma"), "Attach").ClickAsync(new MouseEventArgs());

        Assert.False(dialog.Result.IsCompleted);

        Snackbar.Verify(snackbar => snackbar.Add(
            "Could not attach it to that expense. That expense already came from a bank transaction.",
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);
        Snackbar.Verify(snackbar => snackbar.Add(It.IsAny<string>(), Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Never);

        // Still both rows, and none of the buttons left disabled by the attempt.
        Assert.Equal(["Trattoria Roma", "Pizzeria Napoli"], Titles(provider));
        Assert.All(provider.FindAll(".gs-row button"), button => Assert.False(button.HasAttribute("disabled")));
    }

    [Fact]
    public async Task A_refused_dismissal_keeps_the_row_on_the_list()
    {
        _inbox
            .Setup(client => client.DismissBankTransactionMatchAsync(It.IsAny<Guid>(),
                It.IsAny<DismissBankMatchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(404, ErrorCodes.BankTransactionNotFound));

        var (provider, dialog) = await OpenAsync();

        await Button(RowOf(provider, "Trattoria Roma"), "Not the same").ClickAsync(new MouseEventArgs());

        Assert.False(dialog.Result.IsCompleted);
        Assert.Equal(["Trattoria Roma", "Pizzeria Napoli"], Titles(provider));

        Snackbar.Verify(snackbar => snackbar.Add(
            "Could not put that suggestion away. That imported transaction is no longer in your inbox.",
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);
    }
}
