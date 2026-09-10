using GroupSplit.API.Services.Banking;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
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
    private const string Prefix = "/webhooks";

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
                    AppDbContext dbContext,
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

                    await Apply(notification, connection, dbContext, jobs, logger, ct);

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
    /// What each kind of notification means here.
    /// </summary>
    /// <remarks>
    /// A status is written and saved, and saved before any sync is asked for. Both halves
    /// matter.
    /// <para>
    /// Saved, because these notifications are the only thing in the application that can
    /// move a connection out of <see cref="BankConnectionStatus.LoginRequired"/>. Repairing
    /// one opens the provider's UI in update mode, which hands back no public token, so
    /// nothing on the linking path runs and <c>LOGIN_REPAIRED</c> is the whole of the way
    /// back. A status left sitting in the change tracker means somebody who has just signed
    /// in again is told to sign in again, and goes on being told that.
    /// </para>
    /// <para>
    /// And before, because a sync is dispatched to a worker that reads the connection
    /// afresh. Asking for one while the status it depends on is uncommitted is a race the
    /// sync loses: it reads the old status, answers <c>NotSyncable</c>, and everything
    /// waiting behind the cursor stays there until the nightly sweep.
    /// </para>
    /// </remarks>
    private static async Task Apply(WebhookEvent notification, BankConnection connection, AppDbContext dbContext,
        IJobDispatcher jobs, ILogger logger, CancellationToken ct)
    {
        switch (notification)
        {
            case SyncUpdatesAvailable:
                await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);
                break;

            // Plaid cannot hand over an account the person has not shared, so reading the
            // account list here would return exactly what is already stored and a sync would
            // find nothing. What this notification is actually for is putting the person in
            // front of update mode, and the flag is what does that.
            case NewAccountsAvailable:
                connection.AccountsNotShared = true;
                await dbContext.SaveChangesAsync(ct);
                break;

            // Nothing has broken yet, so the status stays Active and syncs keep running.
            // The flag turns avoidable maintenance into something the person can see, which
            // is the whole difference between this and the outage it becomes if ignored.
            case SignInWillExpire expiring:
                logger.LogInformation(
                    "Bank connection {ConnectionId} will need a new sign-in: {Reason}.",
                    connection.Id, expiring.Reason);

                connection.SignInExpiring = true;
                await dbContext.SaveChangesAsync(ct);
                break;

            case AccountAccessRevoked revoked:
                await RevokeAccountAsync(connection, revoked.ProviderAccountId, dbContext, logger, ct);
                break;

            case LoginRequired:
                connection.Status = BankConnectionStatus.LoginRequired;
                await dbContext.SaveChangesAsync(ct);
                break;

            case LoginRepaired:
                connection.Status = BankConnectionStatus.Active;

                // Whatever the warning was about, the sign-in it asked for has happened.
                connection.SignInExpiring = false;
                await dbContext.SaveChangesAsync(ct);

                // Whatever arrived while it was broken is waiting behind the cursor. The
                // accounts are not read here: update mode may have added one, and the sync
                // picks that up itself the moment a row names an account it does not know.
                await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);
                break;

            case PermissionRevoked:
                connection.Status = BankConnectionStatus.Revoked;
                await dbContext.SaveChangesAsync(ct);
                break;

            case UnhandledWebhook unhandled:
                // Information, not debug. Everything this application does not act on
                // arrives here, and a level that a production deployment filters out is how
                // the one notification that mattered went unnoticed for the life of a
                // connection.
                logger.LogInformation("Ignoring {Provider} webhook {Code}.", connection.Provider, unhandled.Code);
                break;
        }
    }

    /// <summary>
    /// Marks one account as withdrawn, leaving the connection and its other accounts alone.
    /// </summary>
    /// <remarks>
    /// The rows it already brought in stay: that is still where the money went. What stops
    /// is anything new, and saying so is the point -- an account nobody marks goes on
    /// looking healthy while its data quietly stops being real.
    /// </remarks>
    private static async Task RevokeAccountAsync(
        BankConnection connection,
        string providerAccountId,
        AppDbContext dbContext,
        ILogger logger,
        CancellationToken ct)
    {
        await dbContext.Entry(connection).Collection(c => c.Accounts).LoadAsync(ct);

        var account = connection.Accounts
            .FirstOrDefault(candidate => candidate.ProviderAccountId == providerAccountId);

        if (account is null)
        {
            // An account this connection never had. Not an error -- it is one the person
            // never shared -- but it is the same situation the flag exists for, and the
            // provider has just confirmed the account is theirs and not ours.
            logger.LogInformation(
                "Bank connection {ConnectionId}: access was withdrawn from an account it does not know.",
                connection.Id);

            connection.AccountsNotShared = true;
        }
        else
        {
            account.AccessRevoked = true;
        }

        await dbContext.SaveChangesAsync(ct);
    }
}
