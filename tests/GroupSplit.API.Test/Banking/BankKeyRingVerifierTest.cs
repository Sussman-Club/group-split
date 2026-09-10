using GroupSplit.API.Services.Banking;
using Microsoft.AspNetCore.DataProtection;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The startup check that decides whether this deployment can still read the bank access
/// tokens it has already stored.
/// </summary>
/// <remarks>
/// It is the last thing between a wrong certificate and a set of items stranded at the
/// provider: a token that cannot be read can be neither synced, nor repaired in update
/// mode, nor removed -- every one of those hands the token back to the provider. So the
/// interesting cases are not the happy one. They are the four ways it declines to stop the
/// deployment, and the one way it does.
/// </remarks>
public class BankKeyRingVerifierTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_ring_that_reads_what_is_stored_starts_and_says_so()
    {
        using var host = Host(wrapped: true);

        await StoreAsync(host, Protected(host, "access-sandbox-de3ce8ef"));

        await host.VerifyBankKeyRing(Ct);

        Assert.Contains(Logs(host), line => line.Contains("reads what is stored"));
    }

    /// <summary>
    /// The case the whole class exists for: a well-formed certificate that is not the one
    /// the ring was wrapped with. Every other check in the deployment passes it.
    /// </summary>
    [Fact]
    public async Task A_wrapped_ring_that_cannot_read_a_stored_token_refuses_to_start()
    {
        using var host = Host(wrapped: true);

        await StoreAsync(host, "not-a-protected-token");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => host.VerifyBankKeyRing(Ct));

        // The message has to name the way out, because the person reading it at three in
        // the morning cannot recover the tokens from anywhere else.
        Assert.Contains("KeyRingCertificate", e.Message);
        Assert.Contains("restore the previous one", e.Message);
    }

    /// <summary>
    /// With no certificate, an unreadable value is as likely to be the seeder's placeholder
    /// as a real key problem, and nothing here can tell the two apart -- so it must not
    /// stop a development host from starting.
    /// </summary>
    [Fact]
    public async Task An_unwrapped_ring_that_cannot_read_a_token_warns_rather_than_refusing()
    {
        using var host = Host(wrapped: false);

        await StoreAsync(host, "not-a-protected-token");

        await host.VerifyBankKeyRing(Ct);

        Assert.Contains(Logs(host), line => line.Contains("as likely to be seeded development data"));
    }

    /// <summary>
    /// Said out loud rather than passed over, because the failure being guarded against is
    /// silence: a deployment that forgot the certificate looks exactly like one that has it.
    /// </summary>
    [Fact]
    public async Task An_unwrapped_ring_says_the_tokens_sit_beside_the_key_that_opens_them()
    {
        using var host = Host(wrapped: false);

        await host.VerifyBankKeyRing(Ct);

        Assert.Contains(Logs(host), line => line.Contains("stored unwrapped"));
    }

    /// <summary>
    /// A deployment with no connections has nothing to verify, and absence of tokens is not
    /// evidence of a bad key.
    /// </summary>
    [Fact]
    public async Task A_deployment_with_no_connections_yet_starts_without_a_verdict()
    {
        using var host = Host(wrapped: true);

        await host.VerifyBankKeyRing(Ct);

        Assert.DoesNotContain(Logs(host), line => line.Contains("reads what is stored"));
        Assert.DoesNotContain(Logs(host), line => line.Contains("could not be read"));
    }

    /// <summary>
    /// A database that is not there is the ordinary case before the first migration -- and
    /// also what a deployment looks like when the database was briefly away at startup.
    /// Warned about either way, because skipping the check silently is the shape of failure
    /// this exists to end.
    /// </summary>
    [Fact]
    public async Task A_database_that_cannot_be_read_warns_and_does_not_stop_the_host()
    {
        using var host = Host(wrapped: true, database: false);

        await host.VerifyBankKeyRing(Ct);

        Assert.Contains(Logs(host), line => line.Contains("was not checked against one"));
    }

    private static string Protected(IHost host, string token) =>
        host.Services.GetRequiredService<IAccessTokenProtector>().Protect(token);

    private static async Task StoreAsync(IHost host, string ciphertext)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.Add(new BankConnection
        {
            UserId = Guid.NewGuid(),
            Provider = FakeBankConnector.Name,
            ProviderItemId = "item-1",
            InstitutionName = "Fake Bank",
            AccessTokenCiphertext = ciphertext,
            LinkedAt = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync(Ct);
    }

    private static List<string> Logs(IHost host) =>
        host.Services.GetRequiredService<CapturedLogs>().Lines;

    /// <summary>
    /// A host with just enough in it for the check: the configuration it reads the
    /// certificate's presence from, a protector, and a database it can query -- or, for the
    /// last case, one that throws when queried.
    /// </summary>
    private static IHost Host(bool wrapped, bool database = true)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Test" });

        if (wrapped)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{BankingOptions.SectionName}:{nameof(BankingOptions.KeyRingCertificate)}"] = "a-certificate"
            });
        }

        var captured = new CapturedLogs();

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(captured);
        builder.Services.AddSingleton(captured);

        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton<IAccessTokenProtector, DataProtectionAccessTokenProtector>();

        if (database)
        {
            // Named once, outside the lambda: the lambda runs per context, so generating
            // the name inside it would give every scope a database of its own and the
            // check would find nothing stored however much had been written.
            var name = $"keyring-{Guid.NewGuid():N}";

            builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(name));
        }
        else
        {
            // A context with no provider configured throws on the first query, which is
            // what a database that is not there looks like from in here.
            builder.Services.AddDbContext<AppDbContext>(options => { });
        }

        return builder.Build();
    }

    /// <summary>Every line the check wrote, which is most of what it does.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Writer(Lines);

        public void Dispose()
        {
        }

        private sealed class Writer(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                    lines.Add(formatter(state, exception));
            }
        }
    }
}
