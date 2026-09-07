using GroupSplit.API.Services.Banking;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// Where a bank provider tells us something has changed.
/// </summary>
/// <remarks>
/// The only anonymous route in the API, and it has to be: the caller is somebody else's
/// server, which has no account here and no token. What stands in for authentication is the
/// provider's signature over the exact bytes it sent, checked by that provider's connector
/// before anything else looks at the body.
/// <para>
/// The provider is a path segment rather than a header, so a second provider is a second
/// key in the container and no change here, and so an unknown one is answered by routing
/// rather than by parsing something nobody can verify.
/// </para>
/// </remarks>
public static class WebhooksApi
{
    private const string Prefix = "/webhooks";

    /// <summary>The path a provider should be told to send its webhooks to.</summary>
    public static string PathFor(string provider) => $"{Prefix}/{provider}";

    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapWebhooksApi()
        {
            var group = routes.MapGroup(Prefix);

            group.WithTags("Webhooks");

            group.MapProviderWebhook();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapProviderWebhook()
        {
            return group.MapPost("{provider}", async (
                    string provider,
                    HttpContext httpContext,
                    IServiceProvider services,
                    IBankConnectionService connections,
                    IJobDispatcher jobs,
                    ILoggerFactory loggerFactory,
                    CancellationToken ct) =>
                {
                    var logger = loggerFactory.CreateLogger(typeof(WebhooksApi));

                    if (services.GetKeyedService<IBankConnector>(provider) is not { } connector)
                    {
                        logger.LogWarning("A webhook arrived for provider {Provider}, which nothing here speaks.", provider);
                        return Results.NotFound();
                    }

                    // The raw bytes, because the signature is over exactly these and a
                    // round trip through a deserializer would not reproduce them.
                    httpContext.Request.EnableBuffering();
                    using var reader = new StreamReader(httpContext.Request.Body, leaveOpen: true);
                    var body = await reader.ReadToEndAsync(ct);

                    var headers = httpContext.Request.Headers
                        .ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);

                    if (!await connector.VerifyWebhookAsync(headers, body, ct))
                    {
                        // Nothing is parsed, nothing is logged from the body, and the answer
                        // says only that it was refused: an unverified caller learns nothing
                        // about what this endpoint would have done.
                        logger.LogWarning("A {Provider} webhook failed verification and was refused.", provider);
                        return Results.Unauthorized();
                    }

                    var notification = connector.ParseWebhook(body);
                    var connection = await connections.ForProviderItem(provider, notification.ProviderItemId, ct);

                    if (connection is null)
                    {
                        // An item this deployment does not know: a sandbox left over, or a
                        // connection since unlinked. Acknowledged, so the provider stops
                        // retrying something that will never mean anything here.
                        logger.LogInformation("A {Provider} webhook named an item this deployment does not have.", provider);
                        return Results.Ok();
                    }

                    await Apply(notification, connection, jobs, logger, ct);

                    return Results.Ok();
                })
                .AllowAnonymous()
                .WithName("ReceiveProviderWebhook")
                .Produces(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                // Excluded from the generated client: nothing in this app calls it, and a
                // typed method for it would only invite somebody to.
                .ExcludeFromDescription();
        }
    }

    /// <summary>
    /// What each kind of notification means here. Only four do anything; the rest are
    /// acknowledged so the provider stops resending them.
    /// </summary>
    private static async Task Apply(WebhookEvent notification, BankConnection connection, IJobDispatcher jobs,
        ILogger logger, CancellationToken ct)
    {
        switch (notification)
        {
            case SyncUpdatesAvailable:
                await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);
                break;

            case LoginRequired:
                connection.Status = BankConnectionStatus.LoginRequired;
                break;

            case LoginRepaired:
                connection.Status = BankConnectionStatus.Active;
                // Whatever arrived while it was broken is waiting behind the cursor.
                await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);
                break;

            case PermissionRevoked:
                connection.Status = BankConnectionStatus.Revoked;
                break;

            case UnhandledWebhook unhandled:
                logger.LogDebug("Ignoring {Provider} webhook {Code}.", connection.Provider, unhandled.Code);
                break;
        }
    }
}
