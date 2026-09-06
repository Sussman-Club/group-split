using GroupSplit.API.Services.Banking;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Extensions;

/// <summary>
/// The import side of the seam: the sync engine, its queue and worker, and the protector
/// the access tokens go through. Connectors register separately, one per provider, keyed
/// by <see cref="IBankConnector.Provider"/>.
/// </summary>
/// <remarks>
/// Data Protection is added here without a key ring, which is the framework's default.
/// The host that means it to persist says where -- <c>Program.cs</c> puts the ring in the
/// app database; the test hosts make it ephemeral -- and a second <c>AddDataProtection</c>
/// call configures the same builder, so the order does not matter.
/// </remarks>
public static class BankingServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddBankingServices()
        {
            services.AddLogging();
            services.AddDataProtection();
            services.TryAddSingleton(TimeProvider.System);

            services.AddSingleton<BankSyncLocks>();
            services.AddSingleton<BankSyncQueue>();
            services.AddHostedService<BankSyncWorker>();

            services.AddScoped<IAccessTokenProtector, DataProtectionAccessTokenProtector>();
            services.AddScoped<IBankSyncService, BankSyncService>();

            return services;
        }
    }
}
