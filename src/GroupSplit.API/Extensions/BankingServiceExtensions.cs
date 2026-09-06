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
/// Data Protection is added without a key ring, which is the framework's default. The host
/// that means it to persist says where -- <c>Program.cs</c> puts the ring in the app
/// database; the test hosts make it ephemeral -- and a second <c>AddDataProtection</c> call
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
            services.AddOptions<BankingOptions>().BindConfiguration(BankingOptions.SectionName);
            services.AddDataProtection();
            services.TryAddSingleton(TimeProvider.System);

            services.AddSingleton<BankSyncLocks>();
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
