using GroupSplit.API.Errors;
using GroupSplit.API.Services.Banking;
using GroupSplit.Shared;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The banks a person has linked: listing them, linking one, asking for a sync, and
/// unlinking.
/// </summary>
/// <remarks>
/// Named for what it is rather than for who provides it. The roadmap sketched these as
/// <c>/plaid/...</c> because it was describing Plaid's flow, but the connector seam exists
/// so that a second provider changes nothing above it -- and a route named after the first
/// one would change. The provider travels as a field.
/// </remarks>
public static class BankConnectionsApi
{
    private const string Prefix = "/bank-connections";

    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapBankConnectionsApi()
        {
            var group = routes.MapGroup(Prefix)
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("BankConnections");

            group.MapMine();
            group.MapLinkToken();
            group.MapLink();
            group.MapRefresh();
            group.MapSync();
            group.MapUnlink();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapMine()
        {
            return group.MapGet(string.Empty, async (
                    IBankConnectionService connections,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await connections.Mine(ct));
                })
                .WithName("GetBankConnections")
                .Produces<BankConnectionsResponse>();
        }

        /// <summary>
        /// The address the provider should call back on travels with the token, so every item
        /// is registered against the origin this deployment is served on rather than one
        /// address set once in the provider's dashboard for all of them. Which address that is
        /// belongs to <see cref="BankingOptions.PublicOrigin"/> and not to this request.
        /// </summary>
        private RouteHandlerBuilder MapLinkToken()
        {
            return group.MapPost("link-token", async (
                    LinkTokenRequest request,
                    IBankConnectionService connections,
                    CancellationToken ct) =>
                {
                    var token = await connections.CreateLinkToken(request, redirectUri: null, ct);

                    return Results.Ok(token);
                })
                .WithName("CreateLinkToken")
                .Produces<LinkTokenResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status502BadGateway);
        }

        private RouteHandlerBuilder MapLink()
        {
            return group.MapPost(string.Empty, async (
                    CreateBankConnectionRequest request,
                    IBankConnectionService connections,
                    CancellationToken ct) =>
                {
                    var connection = await connections.Link(request, ct);
                    var response = BankConnectionService.Describe(connection);

                    return Results.Created($"{Prefix}/{connection.Id}", response);
                })
                .WithName("LinkBankConnection")
                .Produces<BankConnectionResponse>(StatusCodes.Status201Created)
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                // Declared, unlike anywhere else, because this route's 500 carries a code
                // worth reading: the bank granted access and storing it failed, and the
                // answer says whether that access was handed back.
                .ProducesProblem(StatusCodes.Status500InternalServerError)
                .ProducesProblem(StatusCodes.Status502BadGateway);
        }

        private RouteHandlerBuilder MapRefresh()
        {
            return group.MapPost("{id:guid}/refresh", async (
                    Guid id,
                    IBankConnectionService connections,
                    CancellationToken ct) =>
                {
                    var connection = await connections.Refresh(id, ct);
                    var response = BankConnectionService.Describe(connection);

                    return Results.Ok(response);
                })
                .WithName("RefreshBankConnection")
                .Produces<BankConnectionResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status502BadGateway);
        }

        /// <summary>
        /// Answers as soon as the sync is queued. Nothing waits on a bank inside a request:
        /// a full history can take a minute or more.
        /// </summary>
        private RouteHandlerBuilder MapSync()
        {
            return group.MapPost("{id:guid}/sync", async (
                    Guid id,
                    IBankConnectionService connections,
                    CancellationToken ct) =>
                {
                    await connections.Sync(id, ct);
                    return Results.Accepted();
                })
                .WithName("SyncBankConnection")
                .Produces(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapUnlink()
        {
            return group.MapDelete("{id:guid}", async (
                    Guid id,
                    IBankConnectionService connections,
                    CancellationToken ct) =>
                {
                    await connections.Unlink(id, ct);
                    return Results.NoContent();
                })
                .WithName("UnlinkBankConnection")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status502BadGateway);
        }
    }
}
