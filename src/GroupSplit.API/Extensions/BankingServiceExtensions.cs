using GroupSplit.API.Services.Banking;
using GroupSplit.Jobs;
using GroupSplit.Jobs.InProcess;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Extensions;

/// <summary>
/// The import side of the seam: the sync engine, the jobs that drive it, the services the
/// endpoints call, and the protector the access tokens go through. Connectors register
/// separately, one per provider, keyed by <see cref="IBankConnector.Provider"/> -- and
/// whether one is registered at all is what "bank sync is available" means.
/// </summary>
/// <remarks>
/// The jobs are declared here and run by whoever the host says. Today that is
/// <c>AddInProcessJobs</c>, called from the same list so the API and the test hosts agree;
/// a host that hands jobs to a queue service instead would make these same <c>AddJob</c>
/// calls and skip that one.
/// <para>
/// Data Protection is added here without a key ring, which is the framework's default and
/// is all a test host needs. Where the ring lives and what wraps it is the deploying
/// host's decision, made in <c>AddBankKeyRing</c>; a second <c>AddDataProtection</c> call
/// configures the same builder, so the order does not matter.
/// </para>
/// </remarks>
public static class BankingServiceExtensions
{
    /// <summary>
    /// How long after start before the first sweep, and then how often. Half a minute so a
    /// restart does not hit every bank at once; a day because the webhooks carry the load
    /// and the sweep only catches what they dropped.
    /// </summary>
    internal static readonly TimeSpan SweepDelay = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan SweepPeriod = TimeSpan.FromDays(1);

    extension(IServiceCollection services)
    {
        public IServiceCollection AddBankingServices()
        {
            services.AddLogging();

            // Defaults only. Binding it to configuration is the host's business, because
            // this list is also used by hosts that have no configuration at all, and a
            // registration that quietly needs one fails when the first thing to read it is
            // constructed rather than when it is registered.
            services.AddOptions<BankingOptions>();

            services.TryAddSingleton(TimeProvider.System);

            services.AddSingleton<BankSyncLocks>();
            services.AddDataProtection();
            services.AddScoped<IAccessTokenProtector, DataProtectionAccessTokenProtector>();

            services.AddScoped<IBankSyncService, BankSyncService>();
            services.AddScoped<IBankConnectionService, BankConnectionService>();
            services.AddScoped<IInboxService, InboxService>();

            services.AddJob<SyncBankConnection, SyncBankConnectionHandler>();
            services.AddJob<SweepBankConnections, SweepBankConnectionsHandler>();
            services.AddRecurringJob<SweepBankConnections>(SweepPeriod, SweepDelay);

            services.AddInProcessJobs();

            return services;
        }
    }
}
