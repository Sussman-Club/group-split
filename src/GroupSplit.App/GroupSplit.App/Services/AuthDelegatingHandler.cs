using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.App.Services;

internal class AuthDelegatingHandler(AuthService authService) : DelegatingHandler
{
    protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (authService.AccessTokenResponse is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authService.AccessTokenResponse.AccessToken);
        }
        return await base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
