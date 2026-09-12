using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The labels a group files its spending under.
/// </summary>
/// <remarks>
/// Returned whole rather than paged: categories are bounded by how many kinds of thing a
/// group buys, and they feed a select rather than a grid. See the listing contract in the
/// roadmap.
/// </remarks>
public static class CategoriesApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapCategoriesApi()
        {
            var group = routes.MapGroup("/categories")
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Categories");

            group.MapListCategories();
            group.MapCreateCategory();
            group.MapUpdateCategory();
            group.MapArchiveCategory();
            group.MapUnarchiveCategory();
            group.MapDeleteCategory();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        /// <param name="includeArchived">
        /// Off by default, so the commonest caller -- a picker on a new expense -- asks for
        /// what the group files under today without having to say so. A screen that manages
        /// categories asks for the rest.
        /// </param>
        private RouteHandlerBuilder MapListCategories()
        {
            return group.MapGet(string.Empty, async (
                    Guid? groupId,
                    bool? includeArchived,
                    ICategoryService categories,
                    CancellationToken ct) =>
                {
                    var query = await categories.List(groupId, includeArchived ?? false, ct);
                    return Results.Ok(await query.SelectDto().ToListAsync(ct));
                })
                .WithName("GetCategories")
                .Produces<List<CategoryResponse>>();
        }

        /// <summary>
        /// Retires a category: it leaves the pickers, and the expenses filed under it go on
        /// naming it.
        /// </summary>
        /// <remarks>
        /// A POST to a sub-resource rather than a field on the update request, the way
        /// archiving a group already works. It is one decision with one consequence, and a
        /// client that wanted to retire a category should not have to restate its name and
        /// its rule to do it -- nor risk a stale copy of either overwriting somebody else's
        /// edit on the way past.
        /// </remarks>
        private RouteHandlerBuilder MapArchiveCategory()
        {
            return group.MapPost("/{id:guid}/archive", async (
                    Guid id,
                    ICategoryService categories,
                    CancellationToken ct) =>
                {
                    var archived = await categories.SetArchived(id, archived: true, ct);
                    return Results.Ok(await Answer(categories, archived.Id, ct));
                })
                .WithName("ArchiveCategory")
                .Produces<CategoryResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapUnarchiveCategory()
        {
            return group.MapPost("/{id:guid}/unarchive", async (
                    Guid id,
                    ICategoryService categories,
                    CancellationToken ct) =>
                {
                    var restored = await categories.SetArchived(id, archived: false, ct);
                    return Results.Ok(await Answer(categories, restored.Id, ct));
                })
                .WithName("UnarchiveCategory")
                .Produces<CategoryResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// The category as the listing renders it, so a client can put the new state
        /// straight into the list it is holding rather than reading the whole thing again.
        /// </summary>
        private static async Task<CategoryResponse?> Answer(ICategoryService categories, Guid id,
            CancellationToken ct)
        {
            var query = await categories.List(null, includeArchived: true, ct);

            return await query.Where(category => category.Id == id).SelectDto().FirstOrDefaultAsync(ct);
        }

        private RouteHandlerBuilder MapCreateCategory()
        {
            return group.MapPost(string.Empty, async (
                    CreateCategoryRequest request,
                    ICategoryService categories,
                    CancellationToken ct) =>
                {
                    var created = await categories.Create(request, ct);
                    return Results.Ok(await Answer(categories, created.Id, ct));
                })
                .WithName("CreateCategory")
                .Produces<CategoryResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapUpdateCategory()
        {
            return group.MapPut("/{id:guid}", async (
                    Guid id,
                    UpdateCategoryRequest request,
                    ICategoryService categories,
                    CancellationToken ct) =>
                {
                    var updated = await categories.Update(id, request, ct);
                    return Results.Ok(await Answer(categories, updated.Id, ct));
                })
                .WithName("UpdateCategory")
                .Produces<CategoryResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapDeleteCategory()
        {
            return group.MapDelete("/{id:guid}", async (
                    Guid id,
                    ICategoryService categories,
                    CancellationToken ct) =>
                {
                    await categories.Delete(id, ct);
                    return Results.Ok();
                })
                .WithName("DeleteCategory")
                .Produces(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }

    extension(IQueryable<Category> categories)
    {
        internal IQueryable<CategoryResponse> SelectDto() =>
            from category in categories
            orderby category.Name
            select new CategoryResponse(
                category.Id,
                category.Group.Id,
                category.Name,
                category.DefaultSplitRuleId,
                category.DefaultSplitRule != null ? category.DefaultSplitRule.Name : null,
                category.ArchivedAt != null);
    }
}
