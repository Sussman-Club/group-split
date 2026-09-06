using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The divisions a group keeps, for its categories to point at.
/// </summary>
public static class SplitRulesApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapSplitRulesApi()
        {
            var group = routes.MapGroup("/split-rules")
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("SplitRules");

            group.MapListSplitRules();
            group.MapGetSplitRule();
            group.MapCreateSplitRule();
            group.MapUpdateSplitRule();
            group.MapDeleteSplitRule();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapListSplitRules()
        {
            return group.MapGet(string.Empty, async (
                    Guid? groupId,
                    ISplitRuleService rules,
                    CancellationToken ct) =>
                {
                    var query = await rules.List(groupId, ct);
                    return Results.Ok(await query.SelectDto().ToListAsync(ct));
                })
                .WithName("GetSplitRules")
                .Produces<List<SplitRuleResponse>>();
        }

        private RouteHandlerBuilder MapGetSplitRule()
        {
            return group.MapGet("/{id:guid}", async (
                    Guid id,
                    ISplitRuleService rules,
                    CancellationToken ct) => Results.Ok(await rules.GetDetails(id, ct)))
                .WithName("GetSplitRule")
                .Produces<SplitRuleDetailsResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapCreateSplitRule()
        {
            return group.MapPost(string.Empty, async (
                    CreateSplitRuleRequest request,
                    ISplitRuleService rules,
                    CancellationToken ct) =>
                {
                    var created = await rules.Create(request, ct);
                    return Results.Ok(await rules.GetDetails(created.Id, ct));
                })
                .WithName("CreateSplitRule")
                .Produces<SplitRuleDetailsResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapUpdateSplitRule()
        {
            return group.MapPut("/{id:guid}", async (
                    Guid id,
                    UpdateSplitRuleRequest request,
                    ISplitRuleService rules,
                    CancellationToken ct) =>
                {
                    var updated = await rules.Update(id, request, ct);
                    return Results.Ok(await rules.GetDetails(updated.Id, ct));
                })
                .WithName("UpdateSplitRule")
                .Produces<SplitRuleDetailsResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapDeleteSplitRule()
        {
            return group.MapDelete("/{id:guid}", async (
                    Guid id,
                    ISplitRuleService rules,
                    CancellationToken ct) =>
                {
                    await rules.Delete(id, ct);
                    return Results.Ok();
                })
                .WithName("DeleteSplitRule")
                .Produces(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }

    extension(IQueryable<SplitRule> rules)
    {
        internal IQueryable<SplitRuleResponse> SelectDto() =>
            from rule in rules
            orderby rule.Name
            select new SplitRuleResponse(rule.Id, rule.Group.Id, rule.Name);
    }
}
