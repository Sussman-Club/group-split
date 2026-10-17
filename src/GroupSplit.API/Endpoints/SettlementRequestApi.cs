using GroupSplit.API.Services;
using GroupSplit.Shared;

namespace GroupSplit.API.Endpoints;

/// <summary>Shareable, recipient-only requests to record repayments made outside the app.</summary>
public static class SettlementRequestApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public IEndpointRouteBuilder MapSettlementRequestsApi()
        {
            var requests = routes.MapGroup("/settlement-requests").RequireAuthorization();
            requests.WithTags("Settlement requests");

            requests.MapPost("", async (CreateSettlementRequest request, ISettlementRequestService service,
                    CancellationToken ct) => Results.Ok(await service.CreateAcrossGroups(request, ct)))
                .WithName("CreateSettlementRequest")
                .Produces<SettlementRequestResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
            requests.MapGet("{token}", async (string token, ISettlementRequestService service, CancellationToken ct) =>
                    Results.Ok(await service.Get(token, ct)))
                .WithName("GetSettlementRequest").Produces<SettlementRequestResponse>()
                .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
            requests.MapPost("{token}/cancel", async (string token, ISettlementRequestService service,
                    CancellationToken ct) => Results.Ok(await service.Cancel(token, ct)))
                .WithName("CancelSettlementRequest").Produces<SettlementRequestResponse>()
                .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
            requests.MapPost("{token}/repayment", async (string token, ISettlementRequestService service,
                    CancellationToken ct) => Results.Ok(await service.RecordRepayment(token, ct)))
                .WithName("RecordSettlementRequestRepayment").Produces<SettlementRequestResponse>()
                .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);

            return routes;
        }
    }

    extension(RouteGroupBuilder groups)
    {
        public RouteHandlerBuilder MapSettlementRequest()
        {
            return groups.MapPost("{groupId:guid}/settlement-requests", async (Guid groupId,
                    CreateSettlementRequest request, ISettlementRequestService service, CancellationToken ct) =>
                    Results.Ok(await service.CreateForGroup(groupId, request, ct)))
                .WithName("CreateGroupSettlementRequest")
                .Produces<SettlementRequestResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }
}
