using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Recording an expense, at the one place where the dialog says something the person could
/// not otherwise know: which rule the category they are about to pick divides by.
/// </summary>
/// <remarks>
/// Issue #245 asked for it in both transaction dialogs, and the two say it in their own
/// code -- this one off <see cref="CategoryResponse.DefaultSplitRuleName"/> directly, the
/// edit dialog off a row it builds -- so one test cannot stand for both.
/// </remarks>
public class CreateTransactionDialogTest : ComponentTest
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<IMerchantsClient> _merchants = new();
    private readonly Mock<ITransactionCommands> _commands = new();

    public CreateTransactionDialogTest()
    {
        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GroupResponse(GroupId, "Trip", 2)]);

        _groups
            .Setup(g => g.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UserInfo(Guid.NewGuid(), "Ana", "Benitez", "ana@example.com")]);

        _merchants
            .Setup(m => m.GetMerchantsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_merchants.Object);
        Services.AddSingleton<IMerchantCommands, MerchantCommands>();

        // Scoped, for the reason UpdateTransactionDialogTest gives: the real
        // TransactionCommands takes IDialogService, which MudBlazor registers scoped.
        Services.AddScoped(_ => _commands.Object);
    }

    /// <summary>
    /// Every category in the list names how it divides, and one that names no rule reads
    /// "even split" rather than nothing.
    /// </summary>
    /// <remarks>
    /// "Groceries" says nothing about how Groceries is divided, and somebody picking it is
    /// choosing both. The blank on a category with no rule was not a missing value either:
    /// no rule means an even split, which is the commonest arrangement in the app and was
    /// the one the dropdown said least about.
    /// </remarks>
    [Fact]
    public async Task The_category_select_says_how_each_category_divides()
    {
        var categories = new[]
        {
            new CategoryResponse(Guid.NewGuid(), GroupId, "Food", null, null),
            new CategoryResponse(Guid.NewGuid(), GroupId, "Rent", Guid.NewGuid(), "By room size")
        };

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(categories);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(GroupId, It.IsAny<CancellationToken>()))
            .Returns(() => categories.ToAsyncEnumerable());

        // The options live in a popover, which needs its host rendered: without one the
        // select shows only what is chosen, and the list under test is never built.
        var popovers = Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("New expense", parameters));

        var select = provider.FindComponents<MudSelect<Guid?>>()
            .Single(component => component.Instance.Label == "Category");

        await provider.InvokeAsync(() => select.Instance.OpenMenu());

        popovers.WaitForAssertion(() =>
        {
            Assert.Contains("By room size", popovers.Markup, StringComparison.Ordinal);
            Assert.Contains("even split", popovers.Markup, StringComparison.Ordinal);
        });
    }
}
