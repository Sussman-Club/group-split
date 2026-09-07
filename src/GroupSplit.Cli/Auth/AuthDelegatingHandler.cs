using System.Net.Http.Headers;

namespace GroupSplit.Cli.Auth;

/// <summary>Attaches the bearer token to every API request.</summary>
public sealed class AuthDelegatingHandler(TokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
