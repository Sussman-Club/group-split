using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group's Activity list, and the one action on it: taking back a settlement.
/// </summary>
/// <remarks>
/// This listing is the only place a transfer is ever shown, so it is the only place one can
/// be removed from -- and until the delete path stopped resolving ids through the expense
/// listing, it could not be removed at all. What is pinned here is that the action belongs
/// to transfers and not to expenses, which are edited and deleted from the grid that owns
/// them, and that declining the confirmation deletes nothing.
/// </remarks>
public class GroupActivityListTest : ComponentTest
{
    private static readonly Guid Group = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ITransactionCommands> _transactions = new();
    private readonly Mock<IDialogService> _dialogs = new();

    private List<GroupActivityResponse> _entries = [];

    /// <summary>What the confirmation dialog comes back with. Yes, unless a test says otherwise.</summary>
    private DialogResult _answer = DialogResult.Ok(true);

    public GroupActivityListTest()
    {
        _groups
            .Setup(client => client.GetGroupActivityAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
                new PagedResponseOfGroupActivityResponse(_entries, 1, 15, _entries.Count));

        // A real MudBlazor reference, already finished with the answer -- the state a
        // component is actually handed. See ConfirmationResultTest for why a stand-in is
        // the wrong tool here.
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<ConfirmationDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<ConfirmationDialog>>(),
                It.IsAny<DialogOptions>()))
            .ReturnsAsync(() =>
            {
                var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);

                reference.Dismiss(_answer);

                return reference;
            });

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_transactions.Object);
        Services.AddSingleton(_dialogs.Object);
    }

    private static GroupActivityResponse Transfer(Guid id, string from = "Anabel", string to = "Daniel",
        decimal amount = 3.60m) => new()
    {
        Id = id,
        Kind = ActivityKind.Transfer,
        Name = "Settlement",
        Amount = amount,
        DateTime = DateTimeOffset.UtcNow,
        PaidByUserId = Guid.NewGuid(),
        PaidByUserName = from,
        PaidToUserId = Guid.NewGuid(),
        PaidToUserName = to
    };

    private static GroupActivityResponse Expense(Guid id, string name = "Dinner") => new()
    {
        Id = id,
        Kind = ActivityKind.Expense,
        Name = name,
        Amount = 40m,
        DateTime = DateTimeOffset.UtcNow,
        PaidByUserId = Guid.NewGuid(),
        PaidByUserName = "Omar"
    };

    private IRenderedComponent<GroupActivityList> Render() =>
        base.Render<GroupActivityList>(parameters => parameters.Add(list => list.GroupId, Group));

    private static IReadOnlyList<IElement> DeleteButtons(IRenderedComponent<GroupActivityList> list) =>
        list.FindAll("button[aria-label^='Delete settlement']");

    [Fact]
    public void A_settlement_offers_a_delete_and_an_expense_does_not()
    {
        _entries = [Transfer(Guid.NewGuid()), Expense(Guid.NewGuid())];

        var list = Render();

        // One button for two rows: the transfer's.
        var button = Assert.Single(DeleteButtons(list));

        // Named for a screen reader, and named after the row rather than "Delete" -- there
        // is one of these per settlement and they are otherwise indistinguishable.
        Assert.Contains("Anabel paid Daniel", button.GetAttribute("aria-label"));
    }

    /// <summary>
    /// The row that carries the button says so in its class, and the row that does not does
    /// not say it.
    /// </summary>
    /// <remarks>
    /// A layout contract rather than a cosmetic one: <c>.gs-row</c> is a three-column grid,
    /// so the fourth child holding the action needs the fourth column that this class adds.
    /// Without it the button lands on an implicit second row, under the entry instead of
    /// beside it.
    /// </remarks>
    [Fact]
    public void Only_a_row_with_an_action_asks_for_the_column_to_put_it_in()
    {
        _entries = [Transfer(Guid.NewGuid()), Expense(Guid.NewGuid())];

        var list = Render();

        var rows = list.FindAll(".gs-row");

        Assert.Equal(2, rows.Count);
        Assert.Contains("gs-row-has-actions", rows[0].ClassName);
        Assert.DoesNotContain("gs-row-has-actions", rows[1].ClassName);
    }

    [Fact]
    public async Task Confirming_deletes_the_settlement()
    {
        var id = Guid.NewGuid();
        _entries = [Transfer(id)];

        var list = Render();

        await list.InvokeAsync(() => DeleteButtons(list).Single().Click());

        // The id it was shown, and named as a settlement so a failure does not report it as
        // an expense that could not be deleted.
        _transactions.Verify(
            commands => commands.DeleteAsync(id, "Settlement", "settlement", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Declining_the_confirmation_deletes_nothing()
    {
        _entries = [Transfer(Guid.NewGuid())];
        _answer = DialogResult.Ok(false);

        var list = Render();

        await list.InvokeAsync(() => DeleteButtons(list).Single().Click());

        _transactions.Verify(
            commands => commands.DeleteAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Cancelling, which is not the same as saying no: the dialog carries no result at all.
    /// The listing has to treat it as a no rather than as a yes or an error.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_confirmation_deletes_nothing()
    {
        _entries = [Transfer(Guid.NewGuid())];
        _answer = DialogResult.Cancel();

        var list = Render();

        await list.InvokeAsync(() => DeleteButtons(list).Single().Click());

        _transactions.Verify(
            commands => commands.DeleteAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
