using GroupSplit.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace GroupSplit.API.Test.Base;

/// <summary>
/// Base class for API tests that provides dependency injection, test database, and mocked HTTP context with an authenticated user.
/// Uses the shared ApiTestFixture for assembly-level configuration.
/// </summary>
public class ApiUnitTest : IAsyncLifetime
{
    /// <summary>
    /// The service provider for this specific test instance.
    /// </summary>
    protected IServiceProvider ServiceProvider { get; private set; } = null!;
    
    /// <summary>
    /// The test database context. Resolved from the service provider.
    /// </summary>
    protected AppDbContext DbContext => ServiceProvider.GetRequiredService<AppDbContext>();
    
    /// <summary>
    /// The identity ID of the authenticated test user for this test.
    /// </summary>
    protected string TestUserId { get; private set; } = null!;
    
    /// <summary>
    /// The shared fixture that contains the service registration template.
    /// </summary>
    protected ApiTestFixture Fixture { get; }
    
    private ServiceProvider? _serviceProviderInstance;
    
    /// <summary>
    /// Constructor that receives the shared fixture from xUnit.
    /// </summary>
    public ApiUnitTest(ApiTestFixture fixture)
    {
        Fixture = fixture;
    }
    
    public async ValueTask InitializeAsync()
    {
        // Generate unique identifiers for this specific test
        var databaseName = $"TestDb_{Guid.NewGuid()}";
        TestUserId = Guid.NewGuid().ToString();
        
        // Clone the service collection template from the fixture
        var services = CloneServiceCollection(Fixture.ServiceCollectionTemplate);
        
        // Register test-specific services (things that must be unique per test)
        
        // 1. Register the database with in-memory provider (unique database per test)
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        
        // 2. Setup and register mocked HTTP context accessor (unique user per test)
        services.AddSingleton<IHttpContextAccessor>(sp => CreateTestHttpContextAccessor(sp, TestUserId));
        
        // Allow derived classes to add more test-specific services
        ConfigureTestServices(services);
        
        // Build the service provider for this test
        _serviceProviderInstance = services.BuildServiceProvider();
        ServiceProvider = _serviceProviderInstance;
        
        // Ensure database is created
        await DbContext.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProviderInstance != null)
        {
            var dbContext = ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.DisposeAsync();
            await _serviceProviderInstance.DisposeAsync();
        }
    }
    
    /// <summary>
    /// Override this method to register test-specific services that should differ per test.
    /// This is called after the fixture's services are cloned but before the provider is built.
    /// </summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
        // Override in derived classes if needed
    }
    
    /// <summary>
    /// Clones a service collection by copying all service descriptors.
    /// </summary>
    private static IServiceCollection CloneServiceCollection(ServiceCollection source)
    {
        var clone = new ServiceCollection();
        foreach (var descriptor in source)
        {
            // Simply add the descriptor itself to preserve all registration details
            ((IList<ServiceDescriptor>)clone).Add(descriptor);
        }
        return clone;
    }
    
    /// <summary>
    /// Creates an HTTP context accessor with an authenticated user.
    /// </summary>
    private static IHttpContextAccessor CreateTestHttpContextAccessor(IServiceProvider sp, string userId)
    {
        var accessor = new HttpContextAccessor();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        };

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        accessor.HttpContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = sp
        };

        return accessor;
    }
    
    /// <summary>
    /// Helper method to get a service from the service provider.
    /// </summary>
    protected T GetService<T>() where T : notnull
    {
        return ServiceProvider.GetRequiredService<T>();
    }
}
