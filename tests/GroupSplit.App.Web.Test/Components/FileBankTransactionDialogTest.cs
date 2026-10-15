using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Filing an imported bank row as an expense: the two failures it shared with the expense
/// dialogs (#323).
/// </summary>
/// <remarks>
/// It divides the same way they do, with the same split control, so it had the same two
/// holes: hand-typed shares that did not add up closed the dialog into the API's refusal,
/// and a group picked while the last one's people were on their way was offered the last
/// one's people.
/// </remarks>
public class FileBankTransactionDialogTest : ComponentTest
{
    private static readonly GroupResponse Flat = new(Guid.NewGuid(), "Flat", 2);
    private static readonly GroupResponse Trip = new(Guid.NewGuid(), "Trip", 2);

    private static readonly UserInfo Ana = new(Guid.NewGuid(), "Ana", "Benitez", "ana@example.com");
    private static readonly UserInfo Daniel = new(Guid.NewGuid(), "Daniel", "Jones", "daniel@example.com");
    private static readonly UserInfo Flatmate = new(Guid.NewGuid(), "Flat", "Mate", null);

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<ITransactionCommands> _commands = new();
    private readonly Mock<IInboxClient> _inbox = new();

    private static readonly Guid Rent = Guid.NewGuid();
    private static readonly Guid Fuel = Guid.NewGuid();

    private static readonly BankTransactionResponse Row = new(
        Guid.NewGuid(), new DateOnly(2026, 10, 1), 100m, "USD", "LIDL 1234", "Lidl", null, null, null,
        null, null, null, null, Pending: false, InboxStatus.New, TransactionId: null, RemovedAt: null,
        "Everyday", "Test Bank");

    public FileBankTransactionDialogTest()
    {
        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Flat, Trip]);
        _groups
            .Setup(g => g.GetGroupMembersAsync(Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Ana, Daniel]);
        _groups
            .Setup(g => g.GetGroupMembersAsync(Flat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Flatmate]);

        _categories
            .Setup(c => c.GetCategoriesAsync(Flat.Id, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(Rent, Flat.Id, "Rent", null, null)]);
        _categories
            .Setup(c => c.GetCategoriesAsync(Trip.Id, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(Fuel, Trip.Id, "Fuel", null, null)]);

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_inbox.Object);
        Services.AddSingleton(Mock.Of<ISplitRuleCommands>());
        Services.AddScoped(_ => _commands.Object);
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> OpenAsync(
        BankTransactionResponse? row = null)
    {
        var provider = Render<MudDialogProvider>();

        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<FileBankTransactionDialog>("File it", new DialogParameters<FileBankTransactionDialog>
                {
                    { dialog => dialog.Row, row ?? Row }
                }));

        return (provider, reference!);
    }

    [Fact]
    public async Task Shares_that_do_not_add_up_are_refused_without_closing()
    {
        var (provider, reference) = await OpenAsync();
        var decisions = provider.FindComponent<ExpenseDecisions>();

        await provider.InvokeAsync(() => decisions.Instance.GroupIdChanged.InvokeAsync(Trip.Id));

        var editor = provider.FindComponent<SplitEditor>();
        await provider.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync(
        [
            new SplitInput { UserId = Ana.Id, Amount = 70m },
            new SplitInput { UserId = Daniel.Id, Amount = 20m }
        ]));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        Assert.False(reference.Result.IsCompleted);

        var remaining = provider.Find(".gs-split-remaining");
        Assert.Contains("Not saved", remaining.TextContent, StringComparison.Ordinal);
        Assert.Contains("$10.00 still to assign.", remaining.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shares_that_add_up_are_filed()
    {
        var (provider, reference) = await OpenAsync();
        var decisions = provider.FindComponent<ExpenseDecisions>();

        await provider.InvokeAsync(() => decisions.Instance.GroupIdChanged.InvokeAsync(Trip.Id));

        var editor = provider.FindComponent<SplitEditor>();
        await provider.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync(
        [
            new SplitInput { UserId = Ana.Id, Amount = 70m },
            new SplitInput { UserId = Daniel.Id, Amount = 30m }
        ]));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        var answer = await reference.GetReturnValueAsync<FileBankTransactionDialog.Answer>();

        Assert.NotNull(answer?.Request);
        Assert.Equal(100m, answer!.Request!.Splits!.Sum(split => split.Amount));
    }

    [Fact]
    public async Task A_group_picked_while_the_last_one_loads_keeps_its_own_people()
    {
        var flatMembers = new TaskCompletionSource<ICollection<UserInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _groups
            .Setup(g => g.GetGroupMembersAsync(Flat.Id, It.IsAny<CancellationToken>()))
            .Returns(flatMembers.Task);

        var (provider, _) = await OpenAsync();
        var decisions = provider.FindComponent<ExpenseDecisions>();

        var pickingFlat = provider.InvokeAsync(() => decisions.Instance.GroupIdChanged.InvokeAsync(Flat.Id));
        await provider.InvokeAsync(() => decisions.Instance.GroupIdChanged.InvokeAsync(Trip.Id));

        flatMembers.SetResult([Flatmate]);
        await pickingFlat;

        var editor = provider.FindComponent<SplitEditor>();
        Assert.Equal([Ana.Id, Daniel.Id], editor.Instance.Members.Select(member => member.Id));
        Assert.Equal(["Fuel"], decisions.Instance.Categories.Select(category => category.Name));
    }

    /// <summary>
    /// The row's suggestion arrives preselected -- group and category both -- and pressing
    /// the button with nothing changed files it there (#437).
    /// </summary>
    [Fact]
    public async Task A_suggested_group_and_category_arrive_preselected()
    {
        var suggested = Row with
        {
            Suggestion = new FilingSuggestionResponse(Row.Id, Trip.Id, Trip.Name, false, Fuel, "Fuel", 0.9)
        };

        var (provider, reference) = await OpenAsync(suggested);

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        var answer = await reference.GetReturnValueAsync<FileBankTransactionDialog.Answer>();

        Assert.Equal(Trip.Id, answer?.Request?.GroupId);
        Assert.Equal(Fuel, answer?.Request?.CategoryId);
    }

    /// <summary>A group picked by hand wins over the suggestion's category.</summary>
    [Fact]
    public async Task Picking_another_group_by_hand_leaves_the_suggestion_behind()
    {
        var suggested = Row with
        {
            Suggestion = new FilingSuggestionResponse(Row.Id, Trip.Id, Trip.Name, false, Fuel, "Fuel", 0.9)
        };

        var (provider, reference) = await OpenAsync(suggested);
        var decisions = provider.FindComponent<ExpenseDecisions>();

        await provider.InvokeAsync(() => decisions.Instance.GroupIdChanged.InvokeAsync(Flat.Id));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        var answer = await reference.GetReturnValueAsync<FileBankTransactionDialog.Answer>();

        Assert.Equal(Flat.Id, answer?.Request?.GroupId);
        Assert.NotEqual(Fuel, answer?.Request?.CategoryId);
    }
}
