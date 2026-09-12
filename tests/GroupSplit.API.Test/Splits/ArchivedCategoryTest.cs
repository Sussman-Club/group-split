using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// Retiring a category a group has stopped filing under.
/// </summary>
/// <remarks>
/// Deleting one is refused as soon as anything is filed under it, and that refusal is right:
/// a category is how a group reads its spending back, and dropping one would take months of
/// that with it. But it leaves a group that has stopped buying takeaway with "Takeaway" in
/// every picker for ever, and the thing they actually meant by "delete this" has no other
/// door. Archiving is that door -- the category leaves the listings, and every expense filed
/// under it goes on naming it.
/// </remarks>
public class ArchivedCategoryTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICategoryService Categories => GetService<ICategoryService>();

    private async Task<(Guid GroupId, Category Category)> AGroupWithACategory()
    {
        var group = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var category = await Categories.Create(
            new CreateCategoryRequest { GroupId = group.Id, Name = "Takeaway" }, Ct);

        return (group.Id, category);
    }

    private async Task<List<Guid>> ListedIn(Guid groupId, bool includeArchived) =>
        await (await Categories.List(groupId, includeArchived, Ct))
            .Select(category => category.Id)
            .ToListAsync(Ct);

    [Fact]
    public async Task An_archived_category_leaves_the_listing()
    {
        var (groupId, category) = await AGroupWithACategory();

        Assert.Contains(category.Id, await ListedIn(groupId, includeArchived: false));

        await Categories.SetArchived(category.Id, archived: true, Ct);

        Assert.DoesNotContain(category.Id, await ListedIn(groupId, includeArchived: false));
    }

    /// <summary>
    /// And is still there for the screen that manages them, or there would be no way back.
    /// </summary>
    [Fact]
    public async Task An_archived_category_is_still_listed_when_it_is_asked_for()
    {
        var (groupId, category) = await AGroupWithACategory();

        await Categories.SetArchived(category.Id, archived: true, Ct);

        Assert.Contains(category.Id, await ListedIn(groupId, includeArchived: true));
    }

    [Fact]
    public async Task Bringing_one_back_puts_it_in_the_listing_again()
    {
        var (groupId, category) = await AGroupWithACategory();

        await Categories.SetArchived(category.Id, archived: true, Ct);
        await Categories.SetArchived(category.Id, archived: false, Ct);

        Assert.Contains(category.Id, await ListedIn(groupId, includeArchived: false));

        var stored = await DbContext.Set<Category>().FindAsync([category.Id], Ct);

        Assert.Null(stored!.ArchivedAt);
    }

    /// <summary>
    /// The whole point: the expenses are still filed under it.
    /// </summary>
    /// <remarks>
    /// This is what somebody is afraid of when they hesitate over the button, and the reason
    /// the alternative -- deleting -- is refused. Nothing about an expense changes.
    /// </remarks>
    [Fact]
    public async Task Expenses_filed_under_an_archived_category_keep_it()
    {
        var (groupId, category) = await AGroupWithACategory();

        var expense = await GetService<ITransactionService>().Create(
            new CreateTransactionRequest
            {
                Name = "Pad thai",
                Amount = 24m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = groupId,
                CategoryId = category.Id,
                PaidByUserId = GetService<ICurrentUser>().User.Id
            },
            Ct);

        await Categories.SetArchived(category.Id, archived: true, Ct);

        var stored = await DbContext.Set<Expense>().FirstAsync(row => row.Id == expense.Id, Ct);

        Assert.Equal(category.Id, stored.CategoryId);
    }

    /// <summary>
    /// Archiving twice is the same category archived, and does not move the date.
    /// </summary>
    /// <remarks>
    /// Since when a group stopped using something is worth knowing -- retired last year and
    /// retired this morning are different things to somebody deciding whether to bring it
    /// back -- and re-stamping it on a second press would quietly destroy that for no
    /// gesture anybody made.
    /// </remarks>
    [Fact]
    public async Task Archiving_an_archived_category_leaves_the_date_alone()
    {
        var (_, category) = await AGroupWithACategory();

        await Categories.SetArchived(category.Id, archived: true, Ct);

        var first = (await DbContext.Set<Category>().FindAsync([category.Id], Ct))!.ArchivedAt;

        DbContext.ChangeTracker.Clear();

        await Categories.SetArchived(category.Id, archived: true, Ct);

        var second = (await DbContext.Set<Category>().FindAsync([category.Id], Ct))!.ArchivedAt;

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Deleting one that is in use is still refused -- and the message now says what to do
    /// instead, because until archiving existed there was nothing to suggest.
    /// </summary>
    [Fact]
    public async Task Deleting_a_category_with_expenses_is_still_refused_and_points_at_archiving()
    {
        var (groupId, category) = await AGroupWithACategory();

        await GetService<ITransactionService>().Create(
            new CreateTransactionRequest
            {
                Name = "Pad thai",
                Amount = 24m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = groupId,
                CategoryId = category.Id,
                PaidByUserId = GetService<ICurrentUser>().User.Id
            },
            Ct);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Categories.Delete(category.Id, Ct));

        Assert.Equal(ErrorCodes.CategoryInUse, refusal.Code);
        Assert.Contains("Archive it instead", refusal.Message, StringComparison.Ordinal);
    }
}
