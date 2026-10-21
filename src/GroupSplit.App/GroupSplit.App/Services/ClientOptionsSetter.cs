using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace GroupSplit.App.Services;

internal sealed class ClientOptionsSetter(NativeOptions options) : IClientOptionsSetter
{
    public void ConfigureClient(HttpClientFactoryOptions factory)
    {
        factory.HttpClientActions.Add(client => client.BaseAddress = options.ApiBaseAddress);
        factory.HttpMessageHandlerBuilderActions.Add(builder =>
        {
            builder.PrimaryHandler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
            builder.AdditionalHandlers.Add(builder.Services.GetRequiredService<AuthDelegatingHandler>());
        });
    }
}
