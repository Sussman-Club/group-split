using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The places money gets spent.
/// </summary>
/// <remarks>
/// Returned whole rather than paged, like the categories: the table is bounded by how many
/// distinct shops a person's banks have ever reported, it feeds a picker rather than a grid,
/// and the search parameter is there for the case where that stops being a small number.
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
                    return Results.Ok(await query.SelectDto().ToListAsync(ct));
                })
                .WithName("GetMerchants")
                .Produces<List<MerchantResponse>>();
        }

        private RouteHandlerBuilder MapGetMerchant()
        {
            return group.MapGet("/{id:guid}", async (
                    Guid id,
                    IMerchantService merchants,
                    CancellationToken ct) =>
                {
                    // Through the service so a missing one refuses the same way everywhere,
                    // then re-read through the projection for the count on the response.
                    var found = await merchants.Get(id, ct);
                    var query = await merchants.List(null, ct);

                    return Results.Ok(await query.Where(merchant => merchant.Id == found.Id)
                        .SelectDto().FirstOrDefaultAsync(ct));
                })
                .WithName("GetMerchant")
                .Produces<MerchantResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapCreateMerchant()
        {
            return group.MapPost(string.Empty, async (
                    CreateMerchantRequest request,
                    IMerchantService merchants,
                    CancellationToken ct) =>
                {
                    var created = await merchants.Create(request, ct);
                    var query = await merchants.List(null, ct);

                    return Results.Ok(await query.Where(merchant => merchant.Id == created.Id)
                        .SelectDto().FirstOrDefaultAsync(ct));
                })
                .WithName("CreateMerchant")
                .Produces<MerchantResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapUpdateMerchant()
        {
            return group.MapPut("/{id:guid}", async (
                    Guid id,
                    UpdateMerchantRequest request,
                    IMerchantService merchants,
                    CancellationToken ct) =>
                {
                    var updated = await merchants.Update(id, request, ct);
                    var query = await merchants.List(null, ct);

                    return Results.Ok(await query.Where(merchant => merchant.Id == updated.Id)
                        .SelectDto().FirstOrDefaultAsync(ct));
                })
                .WithName("UpdateMerchant")
                .Produces<MerchantResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

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
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }

    extension(IQueryable<Merchant> merchants)
    {
        /// <summary>
        /// The wire shape, projected in the database. The count comes back with the row
        /// rather than as a second call per merchant: it is the number that says whether a
        /// rename is a tidy-up or a rewrite, so it has to be on the row being looked at.
        /// </summary>
        internal IQueryable<MerchantResponse> SelectDto() =>
            from merchant in merchants
            orderby merchant.Name
            select new MerchantResponse(
                merchant.Id,
                merchant.Name,
                merchant.LogoUrl,
                merchant.FirstSeenAt,
                merchant.Transactions.Count);
    }
}
