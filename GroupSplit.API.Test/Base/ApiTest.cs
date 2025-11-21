using GroupSplit.Data;
using GroupSplit.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Security.Claims;

namespace GroupSplit.API.Test.Base;

/// <summary>
/// Base class for API tests that provides dependency injection, test database, and mocked HTTP context with an authenticated user.
/// Uses the shared ApiTestFixture for assembly-level configuration.
/// </summary>
public class ApiTest : IAsyncLifetime
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
    public ApiTest(ApiTestFixture fixture)
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
        var mockHttpContextAccessor = CreateMockHttpContextAccessor(TestUserId);
        services.AddSingleton<IHttpContextAccessor>(mockHttpContextAccessor.Object);
        
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
    /// Creates a mocked HTTP context accessor with an authenticated user.
    /// </summary>
    private Mock<IHttpContextAccessor> CreateMockHttpContextAccessor(string userId)
    {
        // Create claims principal with user ID
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        
        // Create a lazy service provider that will be resolved after BuildServiceProvider
        IServiceProvider? serviceProvider = null;
        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider
            .Setup(sp => sp.GetService(It.IsAny<Type>()))
            .Returns<Type>(type =>
            {
                // Lazy resolve from the actual service provider once it's built
                serviceProvider ??= ServiceProvider;
                return serviceProvider?.GetService(type);
            });
        
        // Create mock HTTP context
        var mockHttpContext = new Mock<HttpContext>();
        mockHttpContext.Setup(c => c.User).Returns(claimsPrincipal);
        mockHttpContext.Setup(c => c.RequestServices).Returns(mockServiceProvider.Object);
        mockHttpContext.Setup(c => c.RequestAborted).Returns(CancellationToken.None);
        
        // Setup HTTP context accessor
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(a => a.HttpContext).Returns(mockHttpContext.Object);
        
        return mockHttpContextAccessor;
    }
    
    /// <summary>
    /// Helper method to get a service from the service provider.
    /// </summary>
    protected T GetService<T>() where T : notnull
    {
        return ServiceProvider.GetRequiredService<T>();
    }
}
