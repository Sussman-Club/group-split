using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.Http;

namespace GroupSplit.App.Web.Services;

public class ClientOptionsSetter : IClientOptionsSetter
{
    public void ConfigureClient(HttpClientFactoryOptions options)
    {
        options.HttpClientActions.Add(client => { client.BaseAddress = new Uri("https+http://api"); });

        options.HttpMessageHandlerBuilderActions.Add(b 
            => b.AdditionalHandlers.Add(ActivatorUtilities.GetServiceOrCreateInstance<AuthDelegatingHandler>(b.Services)));
    }
}