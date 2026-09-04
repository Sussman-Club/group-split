using System.Security.Claims;
using System.Text.Encodings.Web;
using GroupSplit.API.Endpoints;
using GroupSplit.API.Extensions;
using GroupSplit.API.Middleware;
using GroupSplit.API.Services;
using GroupSplit.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Test.Base;

/// <summary>
/// Boots the API's real HTTP pipeline — routing, model binding, the authorization
/// policies, the validation filter and <see cref="CurrentUserMiddleware"/> — over a
/// <see cref="TestServer"/>, so a test can send a request and read the status code the
/// endpoints actually produce.
/// <para>
/// The service tests below this reach past all of that and call a service directly, which
/// is why 372 lines of endpoint code sat uncovered and why two of the defects on this
/// branch lived exactly here. Two things are swapped out and nothing else: Keycloak, for a
/// scheme that authenticates whoever the test says, and Postgres, for the in-memory
/// provider the rest of the suite uses. The pipeline in between is the deployed one, put
/// together the same way <c>Program.cs</c> puts it together.
/// </para>
/// </summary>
internal sealed class ApiEndpointHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ApiEndpointHost(WebApplication app, string subject)
    {
        _app = app;

        Client = app.GetTestServer().CreateClient();
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.SubjectHeader, subject);
    }

    /// <summary>A client whose requests arrive authenticated as the host's own user.</summary>
    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// A second client on the same server, authenticated as somebody else. The API
    /// provisions a user on first sight, so this is a real separate account sharing no
    /// group with the first — which is what an authorization test needs.
    /// </summary>
    public HttpClient ClientForAnotherUser()
    {
        var client = _app.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthenticationHandler.SubjectHeader, Guid.NewGuid().ToString());
        return client;
    }

    /// <summary>A client with no credentials at all.</summary>
    public HttpClient AnonymousClient() => _app.GetTestServer().CreateClient();

    public static async Task<ApiEndpointHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName, _ => { });

        builder.Services.AddAuthorizationBuilder();

        // One database per host, so tests cannot see each other's rows.
        var databaseName = $"EndpointDb_{Guid.NewGuid()}";
        builder.Services.AddDbContext<AppDbContext>(
            options => options.UseInMemoryDatabase(databaseName));

        // The same registrations Program.cs makes, minus the ones that need Aspire.
        builder.Services.AddCurrentUser();
        builder.Services.AddScoped<IDebtCalculationService, DebtCalculationService>();
        builder.Services.AddScoped<IAccountService, AccountService>();
        builder.Services.AddScoped<IGroupService, GroupService>();
        builder.Services.AddScoped<ITransactionService, TransactionService>();
        builder.Services.AddRuleVersionServices();
        builder.Services.AddScoped<IRuleService, RuleService>();
        builder.Services.AddValidation();

        var app = builder.Build();

        // Order matters and is the order Program.cs uses: the middleware that resolves the
        // caller runs after authentication and before authorization.
        app.UseAuthentication();
        app.UseMiddleware<CurrentUserMiddleware>();
        app.UseAuthorization();

        app.MapGroupApi();
        app.MapUserApi();
        app.MapTransaction();
        app.MapRulesApi();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        await app.StartAsync(TestContext.Current.CancellationToken);

        return new ApiEndpointHost(app, Guid.NewGuid().ToString());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// Stands in for the Keycloak bearer handler. A request carrying the subject header is
    /// authenticated as that subject and nothing else changes, so the endpoints see the
    /// same claim <see cref="UserProvisioner"/> reads in production; a request without one
    /// is anonymous, which is what makes the 401s in these tests real.
    /// </summary>
    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string SubjectHeader = "X-Test-Subject";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SubjectHeader, out var subject)
                || string.IsNullOrWhiteSpace(subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, subject!),
                    new Claim(ClaimTypes.Email, $"{subject}@example.test")
                ],
                SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
