using System;

namespace GroupSplit.App.Services;

internal sealed class AuthDelegatingHandler(MobileSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await session.AccessTokenAsync(cancellationToken);
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && token is not null)
            await session.InvalidateAsync(token, cancellationToken);
        return response;
    }
}
