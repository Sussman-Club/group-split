using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
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
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Rules");

            group.MapGetRule();
            group.MapCreateRule();
            group.MapUpdateRule();
            group.MapDeleteRule();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapGetRule()
        {
            return group.MapGet("/{ruleId:guid}", async (
                    Guid ruleId,
                    IRuleService ruleService,
                    CancellationToken ct) =>
                {
                    var updateModel = await ruleService.GetRuleDetails(ruleId, ct);
                    return Results.Ok(updateModel);
                })
                .WithName("GetRule")
                .Produces<RuleDetailsResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapCreateRule()
        {
            return group.MapPost(string.Empty, async (
                    CreateRuleRequest request,
                    IRuleService ruleService,
                    CancellationToken ct) =>
                {
                    var version = await ruleService.Create(request, ct);
                    var query = await ruleService.Get(version.Rule.Id, ct);
                    var response = await query.SelectDto().FirstOrDefaultAsync(ct);
                    return Results.Ok(response);
                })
                .WithName("CreateRule")
                .Produces<RuleVersionResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapUpdateRule()
        {
            return group.MapPatch("/{ruleId:guid}", async (
                    Guid ruleId,
                    JsonPatchDocument<UpdateRuleRequest> patchDocument,
                    IRuleService ruleService,
                    CancellationToken ct) =>
                {
                    var ruleDetails = await ruleService.GetRuleDetails(ruleId, ct);
                    var updateModel = new UpdateRuleRequest
                    {
                        Category = ruleDetails.Category,
                        Version = ruleDetails.Version
                    };
                    patchDocument.ApplyTo(updateModel);

                    if (!PatchedModel.IsValid(updateModel, out var invalid))
                        return invalid;
                    await ruleService.Update(ruleId, updateModel, ct);

                    var query = await ruleService.Get(ruleId, ct);
                    var response = await query.SelectDto().FirstOrDefaultAsync(ct);

                    return Results.Ok(response);
                })
                .WithName("UpdateRule")
                .Produces<RuleVersionResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapDeleteRule()
        {
            return group.MapDelete("/{ruleId:guid}", async (
                    Guid ruleId,
                    IRuleService ruleService,
                    CancellationToken ct) =>
                {
                    await ruleService.Delete(ruleId, ct);
                    return Results.Ok();
                })
                .WithName("DeleteRule")
                .Produces(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
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

        internal IQueryable<RuleVersion> ApplyFilter(RuleFilter filter)
        {
            return from version in ruleVersions
                   where (
                             filter.IsSystem == null ||
                             filter.IsSystem ==
                             (
                                 (version.Rule.Flags & (RuleFlags.NonEditable | RuleFlags.NonDeletable))
                                 == (RuleFlags.NonEditable | RuleFlags.NonDeletable)
                             )
                         ) &&
                         (
                             filter.AllowUserTransactions == null ||
                             filter.AllowUserTransactions ==
                             (
                                 (version.Rule.Flags & RuleFlags.NoUserTransactions) == 0
                             )
                         )
                   select version;
        }
    }
}
