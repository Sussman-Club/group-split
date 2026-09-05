using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Shared.Services;

public static class ServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSharedServices(ServiceLifetime sessionLifetime = ServiceLifetime.Scoped)
        {
            services.AddMudTheme();
            services.TryAdd<ThemePreference>(sessionLifetime);
            services.TryAddScoped<ApiErrorPresenter>();
            services.TryAddScoped<LoadGuard>();

            services.TryAdd<TransactionsTracker>(sessionLifetime);
            services.TryAddScoped<ITransactionsPageStateService, TransactionsPageStateService>();

            services.TryAdd<GroupsTracker>(sessionLifetime);
            services.TryAddScoped<IGroupsPageStateService, GroupsPageStateService>();
            
            services.TryAdd<UserTracker>(sessionLifetime);
            services.TryAdd<IUserLogin, UserLogin>(sessionLifetime);

            services.AddApiClient<IUsersClient, UsersClient>();
            services.AddApiClient<IGroupsClient, GroupsClient>();
            services.AddApiClient<ITransactionsClient, TransactionsClient>();
            services.AddApiClient<IRulesClient, RulesClient>();
            
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
        
        private IHttpClientBuilder AddApiClient<TClient, TImplementation>()
            where TClient : class
            where TImplementation : class, TClient
        {
            var builder = services.AddHttpClient<TClient, TImplementation>();
            
            services.AddTransient<IConfigureOptions<HttpClientFactoryOptions>>(sp =>
            {
                var optionsSetter = sp.GetService<IClientOptionsSetter>();

                if (optionsSetter is null)
                {
                    throw new Exception("No client options setter registered.");
                }
                
                return new ConfigureNamedOptions<HttpClientFactoryOptions>(builder.Name, o =>
                {
                    optionsSetter.ConfigureClient(o);
                });
            });
            
            return builder;
        }
    }
}