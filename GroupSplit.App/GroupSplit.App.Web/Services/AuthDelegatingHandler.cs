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
        
        var token = await tokenRefreshService.GetValidAccessTokenAsync(httpContextAccessor.HttpContext, cancellationToken);

        if (token is null)
        {
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