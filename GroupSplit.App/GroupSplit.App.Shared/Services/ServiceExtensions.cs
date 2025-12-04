using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.App.Shared.Services.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.App.Shared.Services;

public static class ServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSharedServices(ServiceLifetime sessionLifetime = ServiceLifetime.Scoped)
        {
            services.AddMudTheme();

            services.TryAdd<TransactionsTracker>(sessionLifetime);
            services.TryAddScoped<ITransactionsPageStateService, TransactionsPageStateService>();

            services.TryAdd<GroupsTracker>(sessionLifetime);
            services.TryAddScoped<IGroupsPageStateService, GroupsPageStateService>();
            
            services.TryAdd<UserTracker>(sessionLifetime);
            services.TryAdd<IUserLogin, UserLogin>(sessionLifetime);

            return services;
        }

        private IServiceCollection TryAdd<TService, TImplementation>(ServiceLifetime lifetime)
            where TImplementation : class, TService
        {
            services.TryAdd(ServiceDescriptor.Describe(typeof(TService), typeof(TImplementation), lifetime));
            return services;
        }

        private IServiceCollection TryAdd<TService>(ServiceLifetime lifetime) where TService : class
        {
            return services.TryAdd<TService, TService>(lifetime);
        }
    }
}