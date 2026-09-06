using GroupSplit.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Data.Splitting.Handlers;

/// <summary>
/// Registers the split-rule handlers and the dispatcher that finds them.
/// </summary>
/// <remarks>
/// In the data project rather than the API's, because the seeder divides expenses too and
/// has its own container. Seed data that divided differently from the way the app divides
/// would hand every developer balances no sequence of user actions could produce.
/// <para>
/// Singletons: dividing a rule needs the rule, the amount, the payer and the membership,
/// and nothing else -- no <c>DbContext</c>, no request. The rule-version handlers are
/// scoped because theirs genuinely do query the database.
/// </para>
/// </remarks>
public static class SplitRuleServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSplitRuleServices()
        {
            services.AddSplitRuleHandler<EvenSplitRule, EvenSplitRuleHandler>();
            services.AddSplitRuleHandler<PercentSplitRule, PercentSplitRuleHandler>();
            services.AddSplitRuleHandler<SharesSplitRule, SharesSplitRuleHandler>();

            // The dispatcher, which knows no kind by name: it makes the generic interface
            // from the rule's own type and asks for it.
            services.TryAddSingleton<ISplitRuleHandler, SplitRuleHandler>();

            return services;
        }

        private IServiceCollection AddSplitRuleHandler<TRule, THandler>()
            where TRule : SplitRule
            where THandler : class, ISplitRuleHandler<TRule>
        {
            services.TryAddSingleton<THandler>();

            services.TryAddSingleton<ISplitRuleHandler<TRule>>(
                provider => provider.GetRequiredService<THandler>());

            return services;
        }
    }
}
