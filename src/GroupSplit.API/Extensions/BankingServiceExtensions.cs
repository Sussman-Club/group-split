using GroupSplit.API.Services.Banking;
using GroupSplit.Jobs;
using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Extensions;

/// <summary>
/// The import side of the seam: the sync engine, the jobs that drive it, the services the
/// endpoints call, and the protector the access tokens go through. Connectors register
/// separately, one per provider, keyed by <see cref="IBankConnector.Provider"/> -- and
/// whether one is registered at all is what "bank sync is available" means.
/// </summary>
/// <remarks>
/// The jobs are declared here and run by whoever the host says. Today that is the jobs
/// seam's own defaults -- an in-memory queue drained by a hosted worker -- taken from the
/// same list so the API and the test hosts agree; a host that hands jobs to a queue service
/// instead would make these same handler registrations and point the dispatcher and
/// receiver at that transport.
/// <para>
/// Data Protection is added here without a key ring, which is the framework's default and
/// is all a test host needs. Where the ring lives and what wraps it is the deploying
/// host's decision, made in <c>AddBankKeyRing</c>; a second <c>AddDataProtection</c> call
/// configures the same builder, so the order does not matter.
/// </para>
/// </remarks>
public static class BankingServiceExtensions
{
    /// <summary>The daily sweep runs at midnight UTC, independently of application startup.</summary>
    internal static readonly JobSchedule.CronSchedule SweepSchedule = JobSchedule.Cron(CronExpression.Daily(), TimeZoneInfo.Utc);

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
            services.AddScoped<IDuplicateMatcher, DuplicateMatcher>();
            services.AddScoped<IInboxService, InboxService>();

            services.AddJobs()
                .Handlers
                    .AddJobHandler<SyncBankConnection, SyncBankConnectionHandler>()
                    .AddJobHandler<SweepBankConnections, SweepBankConnectionsHandler>()
                .JobsBuilder
                    .Scheduler.Add(new SweepBankConnections(),
                        SweepSchedule);

            return services;
        }
    }
}
