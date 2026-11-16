using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;
[McpServerToolType]
public sealed class CategoryTools
{
    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_category_archived", ReadOnly = false, Destructive = false, Idempotent = true), Description("Archive or restore a category; existing expenses keep their category.")]
    public static Task<CategoryResponse> SetCategoryArchived(CategoriesClient client, Guid categoryId, bool archived, CancellationToken ct = default) =>
        archived ? client.ArchiveCategoryAsync(categoryId, ct) : client.UnarchiveCategoryAsync(categoryId, ct);

    [McpServerTool(Name = "list_categories", ReadOnly = true, Destructive = false, Idempotent = true), Description("List accessible categories, optionally for one group.")]
    public static Task<ICollection<CategoryResponse>> ListCategories(CategoriesClient client, Guid? groupId = null, bool includeArchived = false, CancellationToken ct = default) => client.GetCategoriesAsync(groupId, includeArchived, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_category", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a category in a group you belong to.")]
    public static Task<CategoryResponse> CreateCategory(CategoriesClient client, Guid groupId, string name, string description = "", Guid? defaultSplitRuleId = null, CancellationToken ct = default) => client.CreateCategoryAsync(new CreateCategoryRequest { GroupId = groupId, Name = name, Description = description, DefaultSplitRuleId = defaultSplitRuleId }, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "update_category", ReadOnly = false, Destructive = false, Idempotent = true), Description("Update a category. Supply its complete name, description, and default split rule; use null to clear the rule.")]
    public static Task<CategoryResponse> UpdateCategory(CategoriesClient client, Guid categoryId, UpdateCategoryRequest category, CancellationToken ct = default) =>
        client.UpdateCategoryAsync(categoryId, category, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "delete_category", ReadOnly = false, Destructive = true, Idempotent = false), Description("Permanently delete a category after the user confirms. The API refuses deletion if expenses use it.")]
    public static async Task DeleteCategory(CategoriesClient client, Guid categoryId, CancellationToken ct = default) =>
        await client.DeleteCategoryAsync(categoryId, ct);
}
