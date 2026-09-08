using GroupSplit.API.Errors;
using GroupSplit.API.Services.Banking;
using GroupSplit.Shared;
using Microsoft.Extensions.Options;

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
        /// address set once in the provider's dashboard for all of them.
        /// </summary>
        private RouteHandlerBuilder MapLinkToken()
        {
            return group.MapPost("link-token", async (
                    LinkTokenRequest request,
                    HttpContext httpContext,
                    IBankConnectionService connections,
                    IOptions<BankingOptions> options,
                    CancellationToken ct) =>
                {
                    var token = await connections.CreateLinkToken(
                        request,
                        WebhookUrlFor(httpContext, options.Value),
                        redirectUri: null,
                        ct);

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

    /// <summary>
    /// Where the provider should send this deployment's webhooks: the origin it reaches this
    /// deployment on, plus the anonymous path the front end forwards.
    /// </summary>
    /// <remarks>
    /// The origin is <see cref="BankingOptions.PublicOrigin"/>, which a deployment sets, and
    /// not the one the request arrived on: that host is the caller's to choose, and this
    /// address is handed to a provider as where to deliver somebody's bank activity. See the
    /// remarks there for the fallback and why a deployment never relies on it.
    /// </remarks>
    internal static string? WebhookUrlFor(HttpContext httpContext, BankingOptions options)
    {
        var origin = options.PublicOrigin is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : RequestOrigin(httpContext.Request);

        return origin is null
            ? null
            : origin + WebhooksApi.PathFor(options.Provider);
    }

    /// <summary>
    /// The origin this request arrived on, or null over plain HTTP -- which providers refuse
    /// anyway, so a developer running locally gets a link flow that works with no webhooks
    /// rather than a link call refused for an address the provider will not accept.
    /// </summary>
    private static string? RequestOrigin(HttpRequest request) =>
        string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? $"{request.Scheme}://{request.Host}"
            : null;
}