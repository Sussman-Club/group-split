using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Settling;
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
            services.TryAdd<LocalClock>(sessionLifetime);
            services.TryAddScoped<ApiErrorPresenter>();

            // Stateless, and a decision rather than a dependency: registered so the rule
            // editor is handed the answer instead of holding it, and so a host that wants a
            // different one says so here.
            services.TryAddSingleton<IRemainderPolicy, LargestShareRemainderPolicy>();

            services.TryAddScoped<LoadGuard>();
            services.TryAddScoped<DataChangeNotifier>();

            // The one way to write, for pages and dialogs alike. Registered before the page
            // states, which are now readers that delegate their writes here.
            services.TryAddScoped<IGroupCommands, GroupCommands>();
            services.TryAddScoped<ITransactionCommands, TransactionCommands>();
            services.TryAddScoped<IBankCommands, BankCommands>();

            // A category and the split behind it are two aggregates and two commands, and
            // the dialog that edits them together writes to both.
            services.TryAddScoped<ICategoryCommands, CategoryCommands>();
            services.TryAddScoped<ISplitRuleCommands, SplitRuleCommands>();
            services.TryAddScoped<IMerchantCommands, MerchantCommands>();

            // Read by the inbox page and by the nav badge, so one service rather than two
            // that would each fetch the count.
            services.TryAddScoped<InboxStateService>();
            services.TryAddScoped<IInboxStateService>(sp => sp.GetRequiredService<InboxStateService>());
            services.TryAddScoped<IBankLinkLauncher, PlaidLinkLauncher>();

            // Reads what divided an expense, for the dialog that shows one and the dialog
            // that corrects it. A read rather than a write, so it is not a command.
            services.TryAddScoped<DivisionSourceReader>();

            services.TryAdd<TransactionsTracker>(sessionLifetime);
            services.TryAddScoped<ITransactionsPageStateService, TransactionsPageStateService>();

            services.TryAdd<GroupsTracker>(sessionLifetime);
            services.TryAddScoped<IGroupsPageStateService, GroupsPageStateService>();

            // Read by the Settle page, by the home page's waiting strip, and by the nav
            // badge, so one service rather than three that would each fetch the plan.
            services.TryAdd<SettleTracker>(sessionLifetime);
            services.TryAddScoped<ISettleStateService, SettleStateService>();
            
            services.TryAdd<UserTracker>(sessionLifetime);
            services.TryAdd<IUserLogin, UserLogin>(sessionLifetime);

            services.AddApiClient<IUsersClient, UsersClient>();
            services.AddApiClient<IGroupsClient, GroupsClient>();
            services.AddApiClient<ITransactionsClient, TransactionsClient>();
            services.AddApiClient<IInvitationsClient, InvitationsClient>();
            services.AddApiClient<ICategoriesClient, CategoriesClient>();
            services.AddApiClient<ISplitRulesClient, SplitRulesClient>();
            services.AddApiClient<IMerchantsClient, MerchantsClient>();
            services.AddApiClient<IBankConnectionsClient, BankConnectionsClient>();
            services.AddApiClient<IInboxClient, InboxClient>();
            
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