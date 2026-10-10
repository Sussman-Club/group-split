using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using GroupSplit.API.Errors;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SharedProblemDetails = GroupSplit.Shared.ProblemDetails;

namespace GroupSplit.API.Extensions;

/// <summary>
/// How many of each expensive or abusable request one person may make in a window.
/// </summary>
/// <remarks>
/// Configuration rather than constants so a deployment can loosen one without a release, and
/// so a test can tighten one to two. Bound from <c>RateLimits</c>.
/// </remarks>
public sealed class ApiRateLimits
{
    public const string SectionName = "RateLimits";

    /// <summary>
    /// Reading a receipt, from a fresh upload, an expense's attachment or an inbox row's.
    /// Each one is a paid call to Veryfi or Azure OpenAI.
    /// </summary>
    public RateLimit Transcription { get; set; } = new() { PermitLimit = 30, Window = TimeSpan.FromHours(1) };

    /// <summary>
    /// Asking for a link token, exchanging one, refreshing or syncing a connection. Each one
    /// is a call to the bank provider, which bills and throttles per item.
    /// </summary>
    public RateLimit Bank { get; set; } = new() { PermitLimit = 30, Window = TimeSpan.FromHours(1) };

    /// <summary>
    /// Inviting somebody, issuing a guest's link or a join link, and claiming, declining or
    /// accepting one.
    /// </summary>
    public RateLimit Invitations { get; set; } = new() { PermitLimit = 60, Window = TimeSpan.FromHours(1) };
}

public sealed class RateLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }
}

/// <summary>
/// The names endpoints opt into with <c>RequireRateLimiting</c>.
/// </summary>
public static class ApiRateLimitPolicies
{
    public const string Transcription = "transcription";
    public const string Bank = "bank";
    public const string Invitations = "invitations";
}

/// <summary>
/// Per-person limits on the requests that cost money or invite guessing, answered with a
/// <see cref="ErrorCodes.RateLimited"/> problem when one is used up.
/// </summary>
/// <remarks>
/// Partitioned on the token's <c>sub</c> and never on the address. The deployment trusts
/// <c>X-Forwarded-For</c> from whoever sends it, so an address is whatever the caller says it
/// is, and a limit keyed on it is a limit nobody has to respect. Every limited route requires
/// a signed-in caller, and the limiter runs after authorization, so a subject is always there;
/// the shared fallback partition only exists so a route that forgot to require one is
/// throttled as a crowd rather than not at all.
/// <para>
/// In memory, per instance. The API runs as one, and a limit that resets on a deploy is still
/// a limit; a shared store would be the next step if it ever runs as several.
/// </para>
/// </remarks>
public static class RateLimitingExtensions
{
    private const string AnonymousPartition = "anonymous";

    extension(IServiceCollection services)
    {
        public IServiceCollection AddApiRateLimiting()
        {
            services.AddOptions<ApiRateLimits>().BindConfiguration(ApiRateLimits.SectionName);

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = OnRejected;

                options.AddPolicy(ApiRateLimitPolicies.Transcription, context => PerUser(context, limits => limits.Transcription));
                options.AddPolicy(ApiRateLimitPolicies.Bank, context => PerUser(context, limits => limits.Bank));
                options.AddPolicy(ApiRateLimitPolicies.Invitations, context => PerUser(context, limits => limits.Invitations));
            });

            return services;
        }
    }

    extension<TBuilder>(TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        /// <summary>
        /// Puts the route under one of the limits above and declares the 429 it can answer
        /// with, so the generated clients read it as problem details.
        /// </summary>
        public TBuilder RateLimitedAs(string policy)
        {
            builder.RequireRateLimiting(policy);

            if (builder is RouteHandlerBuilder handler)
            {
                handler.ProducesProblem(StatusCodes.Status429TooManyRequests);
            }

            return builder;
        }
    }

    /// <summary>The subject a request is counted against.</summary>
    internal static string PartitionKey(ClaimsPrincipal user) =>
        user.FindFirstValue("sub")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? AnonymousPartition;

    private static RateLimitPartition<string> PerUser(HttpContext context, Func<ApiRateLimits, RateLimit> pick)
    {
        // Read per partition rather than once at startup, so a test host's configuration and
        // a reloaded setting both apply. The factory only runs the first time a subject is
        // seen in a policy, so this is not per request.
        var limit = pick(context.RequestServices.GetRequiredService<IOptions<ApiRateLimits>>().Value);

        return RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context.User), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit.PermitLimit,
            Window = limit.Window,
            // Refused outright rather than queued: holding a request open for most of an hour
            // is worse than telling the caller when to come back.
            QueueLimit = 0,
            AutoReplenishment = true
        });
    }

    private static async ValueTask OnRejected(OnRejectedContext context, CancellationToken ct)
    {
        var httpContext = context.HttpContext;
        int? retryAfter = null;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait))
        {
            // Rounded up: a caller told "0 seconds" retries at once and is refused again.
            retryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = retryAfter.Value.ToString(CultureInfo.InvariantCulture);
        }

        var problem = Problems.Create(
            StatusCodes.Status429TooManyRequests,
            ErrorCodes.RateLimited,
            "Too many requests of this kind. Wait before trying again.",
            new Dictionary<string, object?> { [SharedProblemDetails.RetryAfterExtension] = retryAfter });

        await Problems.WriteAsync(
            httpContext,
            httpContext.RequestServices.GetRequiredService<IProblemDetailsService>(),
            problem,
            cancellationToken: ct);
    }

    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// After authorization, so the caller is known and an anonymous request has already
        /// been answered 401 rather than counted.
        /// </summary>
        public IApplicationBuilder UseApiRateLimiting() => app.UseRateLimiter();
    }
}
