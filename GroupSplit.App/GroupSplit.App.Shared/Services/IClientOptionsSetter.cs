using Microsoft.Extensions.Http;

namespace GroupSplit.App.Shared.Services;

public interface IClientOptionsSetter
{
    void ConfigureClient(HttpClientFactoryOptions options);
}