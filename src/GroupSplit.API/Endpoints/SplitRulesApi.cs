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
            group.MapGetSplitRuleHistory();
            group.MapSetSplitRuleHistory();
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

        /// <summary>
        /// Every division the rule has stood for, so a member can see what an expense from
        /// last March was actually divided by rather than what the rule says today.
        /// </summary>
        private RouteHandlerBuilder MapGetSplitRuleHistory()
        {
            return group.MapGet("/{id:guid}/versions", async (
                    Guid id,
                    ISplitRuleService rules,
                    CancellationToken ct) => Results.Ok(await rules.GetHistory(id, ct)))
                .WithName("GetSplitRuleVersions")
                .Produces<SplitRuleHistoryResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Writes the divisions a rule stood for before it was recorded here, oldest first.
        /// </summary>
        /// <remarks>
        /// A <c>PUT</c> on the same collection the <c>GET</c> above reads, because it states
        /// the whole of it: the rule's history becomes exactly these entries, and sending
        /// them twice writes the same chain rather than two.
        /// <para>
        /// For a rule that has stood for one division since it was made, which is what a
        /// migration leaves behind and what a rule created here starts as. It refuses
        /// anything else, and it refuses a history whose last entry is not the division the
        /// rule stands for now -- that entry becomes the open version, and the open version
        /// is the row every recorded expense already points at. For the same reason it
        /// refuses an entry dated after now: the open version would start in the future,
        /// where a new expense is still divided by it and a reattach no longer finds it.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapSetSplitRuleHistory()
        {
            return group.MapPut("/{id:guid}/versions", async (
                    Guid id,
                    List<SplitRuleVersionInput> versions,
                    ISplitRuleService rules,
                    CancellationToken ct) => Results.Ok(await rules.SetHistory(id, versions, ct)))
                .WithName("SetSplitRuleVersions")
                .Produces<SplitRuleHistoryResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
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
