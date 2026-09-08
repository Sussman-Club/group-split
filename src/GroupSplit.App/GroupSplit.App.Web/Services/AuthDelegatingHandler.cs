namespace GroupSplit.App.Web.Services;

internal class AuthDelegatingHandler(
    IHttpContextAccessor httpContextAccessor,
    TokenRefreshService tokenRefreshService) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated is not true)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                RequestMessage = request
            };
        }
        
        var token = await tokenRefreshService.GetAccessTokenAsync(httpContextAccessor.HttpContext);

        if (token is null)
        {
            // No sign-out here. This runs from wherever a component asked the API for
            // something, which is typically mid-render with the response already on its way
            // out -- and signing out writes a Set-Cookie header, which at that point throws
            // "Headers are read-only". Rejecting the ticket is the cookie handler's job, in
            // OnValidatePrincipal, where the response has not started yet.
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                RequestMessage = request
            };
        }
        
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Sync operations are not supported.");
    }
}