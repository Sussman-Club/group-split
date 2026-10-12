using GroupSplit.API.Services.Banking;
using Microsoft.AspNetCore.Http.Features;

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
    /// <summary>
    /// Public so the host can leave it out of HTTPS redirection: a provider POSTs once and
    /// reads the status code, and a redirect is a failed delivery whatever it points at.
    /// </summary>
    public const string Prefix = "/webhooks";

    /// <summary>
    /// How much of an anonymous caller's body is read before it has proved anything. A
    /// provider's notification is a small JSON object; this is generous by two orders of
    /// magnitude and still refuses a stranger the server's 30MB default.
    /// </summary>
    private const long MaxBodyBytes = 128 * 1024;

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
            // Constrained rather than matched loosely, so a segment that could not name a
            // connector never reaches the handler. It is the one caller-written value on an
            // anonymous route, and unconstrained it reached a log sink verbatim --
            // percent-decoded, newlines and all, as long as a URL allows.
            //
            // Anchored with \A and \z and not with ^ and $. Route constraints match rather
            // than parse, and in .NET a trailing "$" is happy to sit in front of a final
            // newline -- so "plaid%0A" would pass the constraint, miss the connector lookup,
            // and split the log line that says so.
            //
            // And (?-i:...) because route constraints are compiled IgnoreCase, so without it
            // this reads as lower-case only and admits "PLAID". A connector key is
            // lower-case by contract; the pattern should mean what it says.
            return group.MapPost(@"{provider:regex(\A(?-i:[a-z0-9-]+)\z):maxlength(32)}", async (
                    string provider,
                    HttpContext httpContext,
                    IServiceProvider services,
                    IBankConnectionService connections,
                    ILoggerFactory loggerFactory,
                    CancellationToken ct) =>
                {
                    var logger = loggerFactory.CreateLogger(typeof(WebhooksApi));

                    if (services.GetKeyedService<IBankConnector>(provider) is not { } connector)
                    {
                        logger.LogWarning("A webhook arrived for provider {Provider}, which nothing here speaks.", provider);
                        return Results.NotFound();
                    }

                    // Capped before a byte is read. This is the only anonymous route in the
                    // API, and everything below -- reading the body, hashing it, checking the
                    // signature -- happens before the caller has proved anything. On the
                    // server's default a stranger could spend 30MB of disk and twice that in
                    // memory per call, and a provider's notification is a fraction of this.
                    if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                        size.MaxRequestBodySize = MaxBodyBytes;

                    // The raw bytes, because the signature is over exactly these and a
                    // round trip through a deserializer would not reproduce them.
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

                    await connections.ApplyWebhook(notification, connection, ct);

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
}
