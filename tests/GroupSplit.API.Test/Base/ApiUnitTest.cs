using System.Security.Claims;
using GroupSplit.API.Services;
using GroupSplit.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

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
    protected UserClaims TestUserClaims { get; private set; } = null!;

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

    public record UserClaims(string UserId, string Email);

    public async ValueTask InitializeAsync()
    {
        // Generate unique identifiers for this specific test
        var databaseName = $"TestDb_{Guid.NewGuid()}";

        // Clone the service collection template from the fixture
        var services = CloneServiceCollection(Fixture.ServiceCollectionTemplate);

        // Register test-specific services (things that must be unique per test)

        // 1. Register the database with in-memory provider (unique database per test)
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));

        // 2. Setup and register mocked HTTP context accessor (unique user per test)
        services.AddScoped<UserClaims>(sp => new UserClaims(Guid.NewGuid().ToString(), $"testuser_{Guid.NewGuid()}@example.com"));
        services.AddTransient<IHttpContextAccessor>(sp => CreateTestHttpContextAccessor(sp));

        // Allow derived classes to add more test-specific services
        ConfigureTestServices(services);

        // Build the service provider for this test
        _serviceProviderInstance = services.BuildServiceProvider();
        ServiceProvider = _serviceProviderInstance;

        TestUserClaims = ServiceProvider.GetRequiredService<UserClaims>();

        // Ensure database is created
        await DbContext.Database.EnsureCreatedAsync();

        await InitializeCurrentUser(ServiceProvider);
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
    private static IHttpContextAccessor CreateTestHttpContextAccessor(IServiceProvider sp)
    {
        var mock = new Mock<IHttpContextAccessor>();
        mock.Setup(a => a.HttpContext).Returns(() =>
            {
                var userClaims = sp.GetRequiredService<UserClaims>();

                IEnumerable<Claim> claims = [
                    new Claim(ClaimTypes.NameIdentifier, userClaims.UserId),
                    new Claim(ClaimTypes.Email, userClaims.Email)
                ];

                var identity = new ClaimsIdentity(claims, "TestAuth");
                var principal = new ClaimsPrincipal(identity);

                return new DefaultHttpContext
                {
                    User = principal,
                    RequestServices = sp
                };
            });
        return mock.Object;
    }

    /// <summary>
    /// Helper method to get a service from the service provider.
    /// </summary>
    protected T GetService<T>() where T : notnull
    {
        return ServiceProvider.GetRequiredService<T>();
    }

    protected async Task<Data.Entities.User> CreateNewUser()
    {
        using var scope = ServiceProvider.CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);
        return scope.ServiceProvider.GetRequiredService<ICurrentUser>().User;
    }

    internal static async Task InitializeCurrentUser(IServiceProvider services)
    {
        var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException("No test HTTP context is available.");
        var provisioner = services.GetRequiredService<IUserProvisioner>();
        var initializer = services.GetRequiredService<ICurrentUserInitializer>();
        var user = await provisioner.GetOrCreate(httpContext.User, TestContext.Current.CancellationToken);
        initializer.Initialize(user);
    }
}
