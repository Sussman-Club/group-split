using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(ApiTestFixture))]

namespace GroupSplit.API.Test.Base;

/// <summary>
/// Assembly-level fixture that holds shared configuration for all API tests.
/// This is initialized once per test assembly, not per test.
/// </summary>
public class ApiTestFixture : IAsyncLifetime
{
    /// <summary>
    /// Template service collection that can be cloned for each test.
    /// This contains all the service registrations but no actual instances.
    /// </summary>
    public ServiceCollection ServiceCollectionTemplate { get; private set; } = null!;
    
    public ValueTask InitializeAsync()
    {
        // Create the template service collection with all registrations
        ServiceCollectionTemplate = new ServiceCollection();
        
        // Register all your API services here
        // Note: We're just registering, not building the provider yet
        RegisterServices(ServiceCollectionTemplate);
        
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // Nothing to dispose at assembly level
        return ValueTask.CompletedTask;
    }
    
    /// <summary>
    /// Register all application services. Override this in derived fixtures for test-specific services.
    /// </summary>
    protected virtual void RegisterServices(IServiceCollection services)
    {
        // Register your API services
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<ITransactionService, TransactionService>();
            
        // Add more services as your API grows
        // services.AddScoped<ITransactionService, TransactionService>();
    }
}
