using GroupSplit.AppHost.Test.Base;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(AppHostFixture))]

namespace GroupSplit.AppHost.Test.Base;

public class AppHostFixture : IAsyncLifetime
{
    public DistributedApplication Application { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.GroupSplit_AppHost>();

        builder.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        Application = await builder.BuildAsync();
        await Application.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Application.StopAsync();
        await Application.DisposeAsync();
    }
}
