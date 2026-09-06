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
            group.MapDeleteCategory();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapListCategories()
        {
            return group.MapGet(string.Empty, async (
                    Guid? groupId,
                    ICategoryService categories,
                    CancellationToken ct) =>
                {
                    var query = await categories.List(groupId, ct);
                    return Results.Ok(await query.SelectDto().ToListAsync(ct));
                })
                .WithName("GetCategories")
                .Produces<List<CategoryResponse>>();
        }

        private RouteHandlerBuilder MapCreateCategory()
        {
            return group.MapPost(string.Empty, async (
                    CreateCategoryRequest request,
                    ICategoryService categories,
                    CancellationToken ct) =>
                {
                    var created = await categories.Create(request, ct);
                    var query = await categories.List(null, ct);
                    return Results.Ok(await query.Where(category => category.Id == created.Id)
                        .SelectDto().FirstOrDefaultAsync(ct));
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
                    var query = await categories.List(null, ct);
                    return Results.Ok(await query.Where(category => category.Id == updated.Id)
                        .SelectDto().FirstOrDefaultAsync(ct));
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
                category.DefaultSplitRule != null ? category.DefaultSplitRule.Name : null);
    }
}
