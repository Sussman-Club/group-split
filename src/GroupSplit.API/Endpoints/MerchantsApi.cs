using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The places money gets spent.
/// </summary>
/// <remarks>
/// Not paged: a listing is the caller's own places only, and at most twenty of them,
/// because it feeds a type-ahead rather than a grid -- a search that matches more wants
/// another letter, not a second page. See <see cref="MerchantService"/> for both limits.
/// </remarks>
public static class MerchantsApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapMerchantsApi()
        {
            var group = routes.MapGroup("/merchants")
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Merchants");

            group.MapListMerchants();
            group.MapGetMerchant();
            group.MapCreateMerchant();
            group.MapUpdateMerchant();
            group.MapDeleteMerchant();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapListMerchants()
        {
            return group.MapGet(string.Empty, async (
                    string? search,
                    IMerchantService merchants,
                    CancellationToken ct) =>
                {
                    var query = await merchants.List(search, ct);
                    return Results.Ok(await query.ToListAsync(ct));
                })
                .WithName("GetMerchants")
                .Produces<List<MerchantResponse>>()
                .ProducesValidationProblem();
        }

        private RouteHandlerBuilder MapGetMerchant()
        {
            return group.MapGet("/{id:guid}", async (
                    Guid id,
                    IMerchantService merchants,
                    CancellationToken ct) => Results.Ok(await merchants.Get(id, ct)))
                .WithName("GetMerchant")
                .Produces<MerchantResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapCreateMerchant()
        {
            return group.MapPost(string.Empty, async (
                    CreateMerchantRequest request,
                    IMerchantService merchants,
                    CancellationToken ct) => Results.Ok(await merchants.Create(request, ct)))
                .WithName("CreateMerchant")
                .Produces<MerchantResponse>()
                .ProducesValidationProblem();
        }

        /// <summary>
        /// Refused for every caller today -- see <see cref="MerchantService"/> -- and kept
        /// so that a client asking is told why, with a code it can branch on.
        /// </summary>
        private RouteHandlerBuilder MapUpdateMerchant()
        {
            return group.MapPut("/{id:guid}", async (
                    Guid id,
                    UpdateMerchantRequest request,
                    IMerchantService merchants,
                    CancellationToken ct) =>
                {
                    await merchants.Update(id, request, ct);
                    return Results.Ok(await merchants.Get(id, ct));
                })
                .WithName("UpdateMerchant")
                .Produces<MerchantResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <inheritdoc cref="MapUpdateMerchant"/>
        private RouteHandlerBuilder MapDeleteMerchant()
        {
            return group.MapDelete("/{id:guid}", async (
                    Guid id,
                    IMerchantService merchants,
                    CancellationToken ct) =>
                {
                    await merchants.Delete(id, ct);
                    return Results.Ok();
                })
                .WithName("DeleteMerchant")
                .Produces(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
