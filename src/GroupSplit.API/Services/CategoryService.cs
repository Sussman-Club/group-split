using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ICategoryService
{
    /// <param name="includeArchived">
    /// True to list what the group has retired as well. False -- the default -- answers with
    /// what it files under today, which is what every picker wants.
    /// </param>
    Task<IQueryable<Category>> List(Guid? groupId, bool includeArchived = false,
        CancellationToken ct = default);

    Task<Category> Create(CreateCategoryRequest request, CancellationToken ct = default);
    Task<Category> Update(Guid id, UpdateCategoryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Retires a category, or brings it back.
    /// </summary>
    /// <remarks>
    /// What a group means by "delete this" once it has been used for a year. Deleting is
    /// refused outright while anything is filed under it, and rightly -- a category is how a
    /// group's spending is read back, and dropping one would take months of that with it.
    /// Archiving takes it out of the pickers and leaves every expense still naming it.
    /// </remarks>
    Task<Category> SetArchived(Guid id, bool archived, CancellationToken ct = default);

    Task Delete(Guid id, CancellationToken ct = default);
}

/// <summary>
/// The labels a group files its spending under.
/// </summary>
/// <remarks>
/// Half of what the old rule was. The other half -- how to divide -- is a
/// <see cref="SplitRule"/> a category may point at, which is what lets Groceries, Utilities
/// and Cleaning share one rule and change in one place when a roommate moves out.
/// <para>
/// The rule and not one of its versions, so the two edits stay apart: pointing a category
/// somewhere else says nothing about what any rule contains, and editing a rule says nothing
/// about which categories follow it.
/// </para>
/// </remarks>
public class CategoryService(ICurrentUser userContext, AppDbContext dbContext) : ICategoryService
{
    public Task<IQueryable<Category>> List(Guid? groupId, bool includeArchived = false,
        CancellationToken ct = default)
    {
        var groups = dbContext.Entry(userContext.User).Collection(u => u.Groups).Query();

        var query = from category in dbContext.Set<Category>()
                    where groups.Any(@group => @group.Id == category.Group.Id)
                          && (groupId == null || category.Group.Id == groupId)
                          && (includeArchived || category.ArchivedAt == null)
                    select category;

        return Task.FromResult(query);
    }

    public async Task<Category> Create(CreateCategoryRequest request, CancellationToken ct = default)
    {
        var group = await GroupOfCaller(request.GroupId, ct);

        await RefuseDuplicateName(group.Id, request.Name, null, ct);

        var category = new Category
        {
            Group = group,
            Name = request.Name,
            DefaultSplitRule = await RuleOf(group, request.DefaultSplitRuleId, ct)
        };

        dbContext.Add(category);
        await dbContext.SaveChangesAsync(ct);

        return category;
    }

    public async Task<Category> Update(Guid id, UpdateCategoryRequest request, CancellationToken ct = default)
    {
        var category = await Existing(id, ct);

        await RefuseDuplicateName(category.Group.Id, request.Name, id, ct);

        category.Name = request.Name;

        // Re-pointing a category at a different rule changes what the next expense is
        // pre-filled with, and nothing already recorded: a past expense holds both the
        // amounts it was divided into and the version that divided them, neither of which
        // this touches.
        category.DefaultSplitRule = await RuleOf(category.Group, request.DefaultSplitRuleId, ct);

        await dbContext.SaveChangesAsync(ct);

        return category;
    }

    public async Task<Category> SetArchived(Guid id, bool archived, CancellationToken ct = default)
    {
        var category = await Existing(id, ct);

        // Idempotent on purpose: archiving a category twice is the same category archived,
        // and re-stamping the date would move a record of when the group stopped using it
        // for no reason anybody asked for.
        if (archived == category.ArchivedAt.HasValue)
            return category;

        category.ArchivedAt = archived ? DateTimeOffset.UtcNow : null;

        await dbContext.SaveChangesAsync(ct);

        return category;
    }

    public async Task Delete(Guid id, CancellationToken ct = default)
    {
        var category = await Existing(id, ct);

        var stillUsed = await dbContext.Set<Expense>().AnyAsync(expense => expense.CategoryId == id, ct);

        // The database would refuse this anyway -- the relationship is Restrict, so that a
        // category cannot take the group's spending history with it -- but a foreign-key
        // violation is not something a member can act on.
        if (stillUsed)
            throw new ConflictException(ErrorCodes.CategoryInUse,
                "This category still has expenses filed under it. Archive it instead.");

        dbContext.Remove(category);
        await dbContext.SaveChangesAsync(ct);
    }

    // Archived ones included, or nothing could be brought back: the one call that must find
    // a retired category is the one that un-retires it. Editing and deleting reach through
    // here too, which is right -- a category the group has stopped using is still theirs.
    private async Task<Category> Existing(Guid id, CancellationToken ct) =>
        await (await List(null, includeArchived: true, ct)).Include(category => category.Group)
            .FirstOrDefaultAsync(category => category.Id == id, ct)
        // A category in someone else's group takes this path too, and says what a missing
        // one says: whether it exists is not the caller's to learn.
        ?? throw new NotFoundException(ErrorCodes.CategoryNotFound, "Category not found.");

    private async Task<Group> GroupOfCaller(Guid groupId, CancellationToken ct) =>
        await dbContext.Entry(userContext.User).Collection(u => u.Groups).Query()
            .FirstOrDefaultAsync(@group => @group.Id == groupId, ct)
        ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

    private async Task<SplitRule?> RuleOf(Group group, Guid? splitRuleId, CancellationToken ct)
    {
        if (splitRuleId is null)
            return null;

        return await dbContext.Set<SplitRule>()
                   .FirstOrDefaultAsync(rule => rule.Id == splitRuleId && rule.Group.Id == group.Id, ct)
               ?? throw new NotFoundException(ErrorCodes.SplitRuleNotFound, "Split rule not found.");
    }

    /// <summary>
    /// Categories are picked from a list by name, so two with the same name in one group are
    /// two the member cannot tell apart. Checked here as well as by the unique index, to say
    /// which name rather than which constraint.
    /// </summary>
    private async Task RefuseDuplicateName(Guid groupId, string name, Guid? excluding, CancellationToken ct)
    {
        var taken = await dbContext.Set<Category>().AnyAsync(category =>
            category.Group.Id == groupId &&
            category.Id != excluding &&
            category.Name.ToLower() == name.ToLower(), ct);

        if (taken)
            throw new ConflictException(ErrorCodes.CategoryNameTaken,
                $"This group already has a category called \"{name}\".");
    }
}
