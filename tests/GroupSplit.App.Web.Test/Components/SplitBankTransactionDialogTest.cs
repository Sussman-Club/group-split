using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Sorting one charge into the purchases it turns out to be.
/// </summary>
/// <remarks>
/// What these are about is the one rule the screen exists to hold: the parts have to come to
/// what the card was charged, and until they do there is nothing to file. Everything else --
/// which lines are picked, how many parts there are -- is in service of that.
/// <para>
/// The figures are asked of the API rather than worked out here, so these also pin that the
/// screen sends the placement it is showing. A part priced from a stale request is a number
/// somebody would file against.
/// </para>
/// </remarks>
public class SplitBankTransactionDialogTest : ComponentTest
{
    private static readonly Guid RowId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid Jacket = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<IReceiptCommands> _receipts = new();
    private readonly Mock<IBankCommands> _bank = new();

    /// <summary>The placements the screen has asked to be priced, newest last.</summary>
    private readonly List<SplitChargePreviewRequest> _priced = [];

    private SplitBankTransactionRequest? _filed;

    public SplitBankTransactionDialogTest()
    {
        _groups
            .Setup(client => client.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GroupResponse(GroupId, "The flat", 4)]);

        _categories
            .Setup(client => client.GetCategoriesAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _receipts
            .Setup(commands => commands.ForBankRowAsync(RowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Bill);

        // Stands in for the apportioning without repeating it: every part is worth what its
        // lines come to. The bill here has neither tax nor tip, so that is the real answer.
        _bank
            .Setup(commands => commands.PreviewSplitAsync(RowId, It.IsAny<SplitChargePreviewRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, SplitChargePreviewRequest request, CancellationToken _) =>
            {
                _priced.Add(request);

                var amounts = request.Parts
                    .Select(part => part.ItemIds.Sum(id => id == Groceries ? 60m : 40m))
                    .ToList();

                return new SplitChargePreviewResponse(amounts, 100m, amounts.Sum());
            });

        _bank
            .Setup(commands => commands.SplitAsync(RowId, It.IsAny<SplitBankTransactionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, SplitBankTransactionRequest request, CancellationToken _) =>
            {
                _filed = request;

                return new SplitBankTransactionResponse(RowId, 100m, []);
            });

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_receipts.Object);
        Services.AddSingleton(_bank.Object);
    }

    /// <summary>
    /// It opens with every line in no part, and says how much that is.
    /// </summary>
    /// <remarks>
    /// Nothing is placed on the person's behalf. A screen that guessed would be right about
    /// the ordinary charge and silently wrong about the one it exists for, which is the
    /// charge where the lines are not all the same purchase.
    /// </remarks>
    [Fact]
    public async Task It_opens_with_nothing_placed_and_says_what_is_left()
    {
        var dialog = await OpenAsync();

        Assert.Contains("GROCERIES", dialog.Markup, StringComparison.Ordinal);
        Assert.Contains("JACKET", dialog.Markup, StringComparison.Ordinal);

        // Both lines, said as money rather than as a count: what is missing is an amount,
        // and the amount is what has to reach the charge.
        Assert.Contains("2 lines need a part", dialog.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line picked and sent lands in that part, and the part is priced from the placement
    /// that is on screen.
    /// </summary>
    [Fact]
    public async Task Sending_a_picked_line_to_a_part_prices_that_part()
    {
        var dialog = await OpenAsync();

        await PickAsync(dialog, 0);
        await SendAsync(dialog, 0);

        var asked = _priced[^1];

        Assert.Equal([Groceries], asked.Parts[0].ItemIds);
        Assert.Empty(asked.Parts[1].ItemIds);
    }

    /// <summary>
    /// Filing is refused until every line has a part, however tidy the rest of it looks.
    /// </summary>
    /// <remarks>
    /// The invariant, held on screen rather than left to the API. A line in no part is money
    /// no part accounts for, so the parts stop summing to the charge -- and the refusal for
    /// that arrives after the dialog has closed, where there is nothing left to fix it in.
    /// </remarks>
    [Fact]
    public async Task A_line_in_no_part_keeps_the_charge_from_being_filed()
    {
        var dialog = await OpenAsync();

        await PickAsync(dialog, 0);
        await SendAsync(dialog, 0);

        Assert.Contains("1 line needs a part", dialog.Markup, StringComparison.Ordinal);
        Assert.True(FileButton(dialog).Disabled);
    }

    /// <summary>
    /// Every line in a part, in two parts, and the charge can be filed -- as exactly the
    /// placement on screen.
    /// </summary>
    [Fact]
    public async Task A_bill_placed_into_two_parts_files_as_two_expenses()
    {
        var dialog = await OpenAsync();

        await PickAsync(dialog, 0);
        await SendAsync(dialog, 0);
        await PickAsync(dialog, 1);
        await SendAsync(dialog, 1);

        Assert.Contains("Adds up", dialog.Markup, StringComparison.Ordinal);

        var file = FileButton(dialog);

        Assert.False(file.Disabled);
        Assert.Contains("File as 2 expenses", dialog.Markup, StringComparison.Ordinal);

        await dialog.InvokeAsync(() => file.OnClick.InvokeAsync());

        Assert.NotNull(_filed);
        Assert.Equal(2, _filed.Parts.Count);
        Assert.Equal([Groceries], _filed.Parts[0].ItemIds);
        Assert.Equal([Jacket], _filed.Parts[1].ItemIds);
    }

    /// <summary>
    /// Everything in one part is not a split, and the screen says which move is missing.
    /// </summary>
    /// <remarks>
    /// The API's own rule, shown instead of discovered: filing a charge as one expense is
    /// what the ordinary dialog does. Saying "send lines to two parts" rather than greying
    /// the button in silence is the difference between a rule and a dead end.
    /// </remarks>
    [Fact]
    public async Task Everything_in_one_part_is_not_a_split()
    {
        var dialog = await OpenAsync();

        await PickAsync(dialog, 0);
        await PickAsync(dialog, 1);
        await SendAsync(dialog, 0);

        // Nothing is unplaced, so the invariant holds -- and it still cannot be filed.
        Assert.Contains("Adds up", dialog.Markup, StringComparison.Ordinal);
        Assert.Contains("Send lines to two parts", dialog.Markup, StringComparison.Ordinal);
        Assert.True(FileButton(dialog).Disabled);
    }

    /// <summary>
    /// A charge that may already be recorded cannot be split until somebody says it is not.
    /// </summary>
    /// <remarks>
    /// Held here rather than left to the API's refusal because of what a split costs to undo:
    /// one wrong filing is one expense to delete, and one wrong split is several, in
    /// different groups, each having moved somebody's balance.
    /// </remarks>
    [Fact]
    public async Task A_suspected_duplicate_has_to_be_answered_before_it_can_be_split()
    {
        var dialog = await OpenAsync(Row() with
        {
            PossibleDuplicates =
            [
                new ExpenseMatchResponse(Guid.NewGuid(), "Big shop", 100m, "USD",
                    DateTimeOffset.UtcNow, GroupId, "The flat", "Ana", 0m, 0,
                    MatchConfidence.Confident)
            ]
        });

        await PickAsync(dialog, 0);
        await SendAsync(dialog, 0);
        await PickAsync(dialog, 1);
        await SendAsync(dialog, 1);

        Assert.Contains("Confirm this is not a duplicate", dialog.Markup, StringComparison.Ordinal);
        Assert.True(FileButton(dialog).Disabled);

        var tick = dialog.FindComponents<MudCheckBox<bool>>().Single();

        await dialog.InvokeAsync(() => tick.Instance.ValueChanged.InvokeAsync(true));

        Assert.False(FileButton(dialog).Disabled);

        await dialog.InvokeAsync(() => FileButton(dialog).OnClick.InvokeAsync());

        Assert.NotNull(_filed);
        Assert.True(_filed.FileAnyway);
    }

    // ---- the screen, driven ----------------------------------------------------------

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(BankTransactionResponse? row = null)
    {
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<SplitBankTransactionDialog>
        {
            { dialog => dialog.Row, row ?? Row() }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<SplitBankTransactionDialog>("Split this charge", parameters));

        return provider;
    }

    /// <summary>Clicks a line the way the keyboard does, which is the one path a test can take.</summary>
    /// <remarks>
    /// A mouse picks through pointerdown, and bUnit's synthetic click carries no detail --
    /// which is exactly what the component reads to tell a keyboard's Enter from a mouse
    /// whose picking the pointer handlers have already done.
    /// </remarks>
    private static async Task PickAsync(IRenderedComponent<MudDialogProvider> dialog, int index) =>
        await dialog.FindAll(".gs-bill-line")[index].ClickAsync(new());

    /// <summary>Presses the send button for that part in the bar over the bill.</summary>
    private static async Task SendAsync(IRenderedComponent<MudDialogProvider> dialog, int part) =>
        await dialog.FindAll(".gs-pick-bar .gs-send")[part].ClickAsync(new());

    private static MudButton FileButton(IRenderedComponent<MudDialogProvider> dialog) =>
        dialog.FindComponents<MudButton>()
            .Select(component => component.Instance)
            .Single(button => button.Class?.Contains("gs-btn-pill") == true);

    // ---- the data ---------------------------------------------------------------------

    /// <summary>
    /// The warehouse run: the flat's groceries and a jacket, on one charge, with no tax or
    /// tip -- so what a part is worth is what its lines come to, and these can assert on
    /// placement without restating the apportioning.
    /// </summary>
    private static ReceiptResponse Bill => new(
        Guid.NewGuid(), null, RowId, 100m, 0m, 0m, 100m, 0, false,
        [
            new ReceiptItemResponse(Groceries, "GROCERIES", 60m, 1, 60m, true, null, []),
            new ReceiptItemResponse(Jacket, "JACKET", 40m, 1, 40m, true, null, [])
        ]);

    private static BankTransactionResponse Row() =>
        new(RowId, new DateOnly(2026, 9, 10), 100m, "USD", "COSTCO WHOLESALE", "Costco",
            null, null, null, null, null, null, null, false, InboxStatus.New, [], null,
            "Joint Account", "Tattersall Federal")
        {
            BillLineCount = 2
        };
}
