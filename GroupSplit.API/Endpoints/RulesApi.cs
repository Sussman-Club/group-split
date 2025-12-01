using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

public static class RulesApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapRulesApi()
        {
            var group = routes.MapGroup("/rules")
                .RequireAuthorization();

            group.WithTags("Rules");

            group.MapCreateRule();
            group.MapUpdateRule();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapCreateRule()
        {
            return group.MapPost(string.Empty, async (
                    CreateRuleRequest request,
                    IRuleService ruleService,
                    CancellationToken ct) =>
                {
                    var version = await ruleService.Create(request, ct);
                    var query = await ruleService.Get(version.Id, ct);
                    var response = await query.SelectDto().FirstOrDefaultAsync(ct);
                    return Results.Ok(response);
                })
                .WithName("CreateRule")
                .Produces<RuleVersionResponse>();
        }

        private RouteHandlerBuilder MapUpdateRule()
        {
            return group.MapPatch("/{ruleId:guid}", async (
                    Guid ruleId,
                    JsonPatchDocument<UpdateRuleRequest> patchDocument,
                    IRuleService ruleService,
                    CancellationToken ct) =>
                {
                    var updateModel = await ruleService.GetUpdateModel(ruleId, ct);
                    if (updateModel is null)
                        return Results.NotFound();

                    patchDocument.ApplyTo(updateModel);
                    await ruleService.Update(ruleId, updateModel, ct);

                    var query = await ruleService.Get(ruleId, ct);
                    var response = await query.SelectDto().FirstOrDefaultAsync(ct);

                    return Results.Ok(response);
                })
                .WithName("UpdateRule")
                .Produces<RuleVersionResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }

    extension(IQueryable<RuleVersion> ruleVersions)
    {
        internal IQueryable<RuleVersionResponse> SelectDto()
        {
            return from ruleVersion in ruleVersions
                select new RuleVersionResponse(
                    ruleVersion.Rule.Id,
                    ruleVersion.Id,
                    ruleVersion.Rule.Category);
        }
    }
}