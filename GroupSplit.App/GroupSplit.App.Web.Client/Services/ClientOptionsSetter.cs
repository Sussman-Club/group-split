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
        });
    }
}