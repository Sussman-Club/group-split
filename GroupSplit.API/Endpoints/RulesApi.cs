using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

public static class RulesApi
{
    public static RouteGroupBuilder MapRulesApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/rules")
            .RequireAuthorization();

        group.WithTags("Rules");

        group.MapCreateRule();

        return group;
    }

    private static RouteHandlerBuilder MapCreateRule(this RouteGroupBuilder group)
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

    extension(IQueryable<RuleVersion> ruleVersions)
    {
        internal IQueryable<RuleVersionResponse> SelectDto()
        {
            return from ruleVersion in ruleVersions
                select new RuleVersionResponse(ruleVersion.Rule.Id, ruleVersion.Id, ruleVersion.Rule.Category);
        }
    }
}