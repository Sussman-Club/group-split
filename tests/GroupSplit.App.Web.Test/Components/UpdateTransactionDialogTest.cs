using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class UpdateTransactionDialogTest : ComponentTest
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid FoodCategoryId = Guid.NewGuid();
    private static readonly Guid MeId = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<IUserLogin> _login = new();

    public UpdateTransactionDialogTest()
    {
        _login.SetupGet(l => l.User).Returns(new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"));

        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GroupResponse(GroupId, "Trip", 2)]);

        _groups
            .Setup(g => g.GetGroupsAsAsyncEnumerable(It.IsAny<CancellationToken>()))
            .Returns(() => new[] { new GroupResponse(GroupId, "Trip", 2) }.ToAsyncEnumerable());

        _groups
            .Setup(g => g.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"),
                new UserInfo(Guid.NewGuid(), "Daniel", "Jones", "daniel@example.com")
            ]);

        _groups
            .Setup(g => g.GetGroupMembersAsAsyncEnumerable(GroupId, It.IsAny<CancellationToken>()))
            .Returns(() => new[]
            {
                new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"),
                new UserInfo(Guid.NewGuid(), "Daniel", "Jones", "daniel@example.com")
            }.ToAsyncEnumerable());

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(FoodCategoryId, GroupId, "Food", null, null)]);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(GroupId, It.IsAny<CancellationToken>()))
            .Returns(() => new[] { new CategoryResponse(FoodCategoryId, GroupId, "Food", null, null) }.ToAsyncEnumerable());

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_transactions.Object);
        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_login.Object);
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference DialogRef)> OpenAsync(
        TransactionResponse original)
    {
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<UpdateTransactionDialog>
        {
            { dialog => dialog.Original, original }
        };

        IDialogReference? dialogRef = null;
        await provider.InvokeAsync(async () =>
        {
            dialogRef = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<UpdateTransactionDialog>("Edit Transaction", parameters);
        });

        return (provider, dialogRef!);
    }

    [Fact]
    public async Task When_opened_with_null_category_id_it_hydrates_category_from_transaction_details()
    {
        var transactionId = Guid.NewGuid();

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = transactionId,
                Name = "Dinner",
                Amount = 100m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = GroupId,
                PaidByUserId = MeId,
                CategoryId = FoodCategoryId,
                Category = "Food",
                Splits =
                [
                    new TransactionSplitResponse(MeId, "Ana Benitez", 50m),
                    new TransactionSplitResponse(Guid.NewGuid(), "Daniel Jones", 50m)
                ]
            });

        var original = new TransactionResponse
        {
            Id = transactionId,
            Name = "Dinner",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = GroupId,
            PaidByUserId = MeId,
            CategoryId = null, // Empty from ledger
            Category = "Food"
        };

        var (provider, dialogRef) = await OpenAsync(original);

        // Original.CategoryId should be hydrated from details
        Assert.Equal(FoodCategoryId, original.CategoryId);

        // Submit to verify the patch does not inadvertently wipe the category
        var submitButton = provider.FindAll("button").First(b => b.TextContent.Trim() == "Save");
        await submitButton.ClickAsync(new());

        var result = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();
        Assert.NotNull(result);

        // No fields changed, so patch operations shouldn't remove categoryId
        Assert.DoesNotContain(result.Operations, op => op.path == "/categoryId");
    }

    [Fact]
    public async Task When_category_was_deleted_from_group_it_preserves_category_in_dropdown_list()
    {
        var transactionId = Guid.NewGuid();
        var deletedCatId = Guid.NewGuid();

        // CategoriesClient returns empty (category deleted from group)
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(GroupId, It.IsAny<CancellationToken>()))
            .Returns(() => AsyncEnumerable.Empty<CategoryResponse>());

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = transactionId,
                Name = "Dinner",
                Amount = 100m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = GroupId,
                PaidByUserId = MeId,
                CategoryId = deletedCatId,
                Category = "Archived Food",
                Splits = []
            });

        var original = new TransactionResponse
        {
            Id = transactionId,
            Name = "Dinner",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = GroupId,
            PaidByUserId = MeId,
            CategoryId = deletedCatId,
            Category = "Archived Food"
        };

        var (provider, dialogRef) = await OpenAsync(original);

        // The dialog markup should still render the preserved category name
        Assert.Contains("Archived Food", provider.Markup);
    }
}
