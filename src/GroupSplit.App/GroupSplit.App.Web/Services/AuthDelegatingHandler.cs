using System.Net;
using System.Net.Http.Headers;
using Duende.AccessTokenManagement.OpenIdConnect;

namespace GroupSplit.App.Web.Services;

/// <summary>
/// Puts the session's access token on a server-side call to the API, refreshing it first if
/// it is due.
/// </summary>
/// <remarks>
/// Deliberately not via <c>HttpContext</c>: this usually runs inside an interactive
/// circuit, where the ambient one belongs to the long-lived connection and its token was
/// frozen at page load. <c>IUserAccessor</c> finds the principal in the circuit instead.
/// See <see cref="Authentication.ServerSideTokenStore"/>.
/// </remarks>
internal class AuthDelegatingHandler(
    IUserAccessor userAccessor,
    IUserTokenManager tokenManager,
    ILogger<AuthDelegatingHandler> logger) : DelegatingHandler
{
    /// <summary>
    /// What an authorization server answers when the refresh token it was handed is not one
    /// it will honour: RFC 6749 section 5.2. Against a realm that rotates refresh tokens
    /// one-time-use it is the ordinary end of a session -- idle timeout reached, max
    /// lifespan reached, or the token already exchanged -- and not a fault. Every other
    /// error from the exchange is.
    /// </summary>
    private const string RefusedGrant = "invalid_grant";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var user = await userAccessor.GetCurrentUserAsync(cancellationToken);

        if (user.Identity?.IsAuthenticated is not true)
            return Unauthorized(request);

        var result = await tokenManager.GetAccessTokenAsync(user, ct: cancellationToken);

        if (!result.WasSuccessful(out var token, out var failure))
        {
            // No sign-out here: mid-render the response has started, and signing out
            // writes a Set-Cookie, which then throws "Headers are read-only". Logged so a
            // 401 from here is distinguishable from one the API sent.
            if (string.Equals(failure.Error, RefusedGrant, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Session token was refused for {Method} {Uri}: {Error} ({Description}).",
                    request.Method, request.RequestUri, failure.Error, failure.ErrorDescription);
            }
            else
            {
                logger.LogError(
                    "Access token acquisition failed for {Method} {Uri}: {Error} ({Description}).",
                    request.Method, request.RequestUri, failure.Error, failure.ErrorDescription);
            }

            return Unauthorized(request);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken.ToString());

        return await base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Sync operations are not supported.");
    }

    private static HttpResponseMessage Unauthorized(HttpRequestMessage request) =>
        new(HttpStatusCode.Unauthorized) { RequestMessage = request };
}
