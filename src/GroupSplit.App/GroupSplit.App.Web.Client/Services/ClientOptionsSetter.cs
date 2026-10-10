using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Http;

namespace GroupSplit.App.Web.Client.Services;

public class ClientOptionsSetter(IWebAssemblyHostEnvironment hostEnvironment) : IClientOptionsSetter
{
    public void ConfigureClient(HttpClientFactoryOptions options)
    {
        options.HttpClientActions.Add(client =>
        {
            var uriBuilder = new UriBuilder(hostEnvironment.BaseAddress)
            {
                Path = "api/"
            };

            client.BaseAddress = uriBuilder.Uri;

            // The /api route refuses a write it cannot tell came from this app's pages. The
            // browser's Sec-Fetch-Site says so on its own; this says so in a browser that
            // does not send that. See SameOriginRequest.
            client.DefaultRequestHeaders.Add(SameOriginRequest.HeaderName, SameOriginRequest.HeaderValue);
        });
    }
}
