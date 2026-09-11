using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MudBlazor.Services;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// Every service the app registers can actually be built.
/// </summary>
/// <remarks>
/// The test that was missing. A command registered without the generated client it takes
/// compiles, passes every component test -- those register their own mocked clients -- and
/// then throws at <c>builder.Build()</c> on the host's validation pass, which is the first
/// moment anything looks. That is a whole app that does not start, found by running it.
/// <para>
/// One assertion and the container does the work: <c>ValidateOnBuild</c> walks every
/// descriptor and constructs nothing, so this stays fast and catches a missing registration
/// the day it is written rather than the day somebody presses F5.
/// </para>
/// </remarks>
public class SharedServiceCompositionTest
{
    /// <summary>
    /// What the hosts supply from outside <c>AddSharedServices</c>: Blazor's own services,
    /// MudBlazor's, the HTTP client factory the generated clients are built on, and the two
    /// seams each host fills differently -- how a client is pointed at the API, and how
    /// somebody is signed in. Everything else has to come from the registration under test,
    /// which is the point -- adding a service here to make this pass would be moving the
    /// goalposts rather than fixing anything.
    /// </summary>
    private static ServiceCollection Hosted()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpClient();
        services.AddMudServices();

        services.AddSingleton(Mock.Of<IAuthService>());
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddSingleton(Mock.Of<IClientOptionsSetter>());
        services.AddSingleton<NavigationManager, StubNavigation>();

        return services;
    }

    /// <summary>
    /// Blazor's own, which no container builds: the host supplies it per circuit. Only its
    /// base address is ever read here, because nothing is navigated.
    /// </summary>
    private sealed class StubNavigation : NavigationManager
    {
        public StubNavigation() => Initialize("https://localhost/", "https://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    [Fact]
    public void Every_registered_service_can_be_constructed()
    {
        var services = Hosted();

        services.AddSharedServices();

        // Scopes as well: a singleton that takes a scoped service is a failure this would
        // otherwise miss, and the app has one such pairing already.
        var exception = Record.Exception(() => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        Assert.Null(exception);
    }

    /// <summary>
    /// The clients the commands take are registered, named one by one.
    /// </summary>
    /// <remarks>
    /// The pass above already fails without them, but it fails with a container's account
    /// of it -- three nested exceptions naming a descriptor. This says which client is
    /// missing in its own name, which is the difference between a minute and ten.
    /// </remarks>
    [Theory]
    [InlineData(typeof(IUsersClient))]
    [InlineData(typeof(IGroupsClient))]
    [InlineData(typeof(ITransactionsClient))]
    [InlineData(typeof(IInvitationsClient))]
    [InlineData(typeof(ICategoriesClient))]
    [InlineData(typeof(ISplitRulesClient))]
    [InlineData(typeof(IMerchantsClient))]
    [InlineData(typeof(IBankConnectionsClient))]
    [InlineData(typeof(IInboxClient))]
    public void The_generated_client_is_registered(Type client)
    {
        var services = Hosted();

        services.AddSharedServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService(client));
    }

    /// <summary>
    /// And the command layer over them, which is what a dialog injects.
    /// </summary>
    [Theory]
    [InlineData(typeof(IGroupCommands))]
    [InlineData(typeof(ITransactionCommands))]
    [InlineData(typeof(IBankCommands))]
    [InlineData(typeof(ICategoryCommands))]
    [InlineData(typeof(ISplitRuleCommands))]
    [InlineData(typeof(IMerchantCommands))]
    public void The_command_is_registered_and_resolvable(Type command)
    {
        var services = Hosted();

        services.AddSharedServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService(command));
    }
}
