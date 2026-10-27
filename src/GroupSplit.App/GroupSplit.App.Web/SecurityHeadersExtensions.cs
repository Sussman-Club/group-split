using System.Security.Cryptography;
using GroupSplit.Shared;

namespace GroupSplit.App.Web;

/// <summary>
/// The response headers that tell a browser what this origin may be framed by, load and
/// leak: a content security policy, <c>X-Frame-Options</c>, <c>Referrer-Policy</c>,
/// <c>nosniff</c>, and a year of HSTS.
/// </summary>
/// <remarks>
/// <para>
/// The app is cookie-authenticated and has one-click settle, archive and delete actions, so
/// a page that could be framed by another site could be clicked through by one. Refusing
/// every frame ancestor is the point of this file; the rest of the policy is what a page
/// needs to load and nothing more.
/// </para>
/// <para>
/// Not applied under <c>/idp</c>. Keycloak answers there and sends its own policy -- one
/// that lets its pages frame each other, which its session check needs -- and replacing it
/// with this one would break its sign-in pages rather than protect them.
/// </para>
/// </remarks>
public static class SecurityHeadersExtensions
{
    /// <summary>Where the nonce for this response's inline scripts is kept.</summary>
    private const string NonceItem = "group-split:csp-nonce";

    /// <summary>
    /// Keycloak's path on this origin. Matches <c>WebAppExtensions.KeycloakPrefix</c>.
    /// </summary>
    private const string KeycloakPrefix = "/idp";

    /// <summary>
    /// One year, the figure the HSTS preload list asks for. The default is thirty days.
    /// </summary>
    internal static readonly TimeSpan HstsMaxAge = TimeSpan.FromDays(365);

    /// <summary>Google Fonts: the stylesheet, and the files it points at.</summary>
    private const string FontStylesheets = "https://fonts.googleapis.com";

    private const string FontFiles = "https://fonts.gstatic.com";

    /// <summary>
    /// Plaid Link: its script is loaded on demand from here, it opens its own frame from
    /// here, and that script calls Plaid's API from this page.
    /// </summary>
    private const string PlaidLink = "https://cdn.plaid.com";

    private const string PlaidApi = "https://*.plaid.com";

    extension(WebApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddSecurityHeaders()
        {
            builder.Services.AddHsts(options =>
            {
                options.MaxAge = HstsMaxAge;
                options.IncludeSubDomains = true;
            });

            return builder;
        }
    }

    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Adds the headers to every response this app writes itself, error pages included.
        /// First in the pipeline, so nothing can answer before it.
        /// </summary>
        public IApplicationBuilder UseSecurityHeaders() => app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(KeycloakPrefix))
            {
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

                context.Items[NonceItem] = nonce;

                // At the last moment rather than now: the exception handler clears the
                // headers before it renders the error page, and this has to survive that.
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;

                    headers.ContentSecurityPolicy = ContentSecurityPolicy(nonce, context.Request.Host);
                    headers.XFrameOptions = "DENY";
                    headers.XContentTypeOptions = "nosniff";
                    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
    }

    /// <summary>
    /// The nonce this response's policy allows inline scripts by, for the one inline script
    /// the page has: the import map. Null outside a request the headers were added to.
    /// </summary>
    public static string? CspNonce(HttpContext? context) =>
        context?.Items.TryGetValue(NonceItem, out var nonce) is true ? nonce as string : null;

    /// <summary>The policy, one directive per line of the list below.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>script-src</c>: this origin, the import map by its nonce, Plaid Link, and
    /// <c>'wasm-unsafe-eval'</c>, which is what lets WebAssembly compile the .NET runtime.
    /// It allows no JavaScript <c>eval</c>; WebAssembly needs nothing else.</item>
    /// <item><c>style-src</c>: <c>'unsafe-inline'</c> because MudBlazor writes its theme into a
    /// <c>&lt;style&gt;</c> element and positions popovers with <c>style</c> attributes, and
    /// Blazor's reconnect overlay does the same. Inline style cannot run code.</item>
    /// <item><c>connect-src</c>: this origin, and its WebSocket by name, which the
    /// server-rendered mode keeps open. <c>'self'</c> covers that in current browsers and
    /// not in every one still in use.</item>
    /// <item><c>img-src</c>: the bundled marks, the Plaid logo hosts
    /// <see cref="MerchantLogos.ProviderHosts"/> allows, and <c>data:</c> and <c>blob:</c>
    /// for previews of a file not uploaded yet.</item>
    /// <item><c>frame-ancestors 'none'</c>: no page anywhere may frame this one.</item>
    /// </list>
    /// No <c>form-action</c>: signing out posts a form here that redirects to Keycloak, which
    /// is a different origin in development, and browsers apply the directive to the
    /// redirect as well.
    /// </remarks>
    internal static string ContentSecurityPolicy(string nonce, HostString host)
    {
        var sockets = host.HasValue ? $" wss://{host} ws://{host}" : string.Empty;
        var logos = string.Join(' ', MerchantLogos.ProviderHosts.Order(StringComparer.Ordinal).Select(h => $"https://{h}"));

        return string.Join("; ",
            "default-src 'self'",
            $"script-src 'self' 'nonce-{nonce}' 'wasm-unsafe-eval' {PlaidLink}",
            $"style-src 'self' 'unsafe-inline' {FontStylesheets}",
            $"font-src 'self' data: {FontFiles}",
            $"img-src 'self' data: blob: {logos}",
            $"connect-src 'self'{sockets} {PlaidApi}",
            $"frame-src {PlaidLink}",
            "frame-ancestors 'none'",
            "object-src 'none'",
            "worker-src 'self'",
            "base-uri 'self'");
    }
}
