using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Registers the split-rule handlers and the dispatcher that finds them.
/// </summary>
/// <remarks>
/// Beside <see cref="RuleVersionServiceExtensions"/>, because they are the same thing for
/// the same reason and there is no sense in one being somewhere else. The seeder reaches
/// them here as it reaches everything else in this project.
/// <para>
/// Singletons, unlike the rule-version handlers: dividing a rule needs the rule, the
/// amount, the payer and the membership, and nothing else -- no <c>DbContext</c>, no
/// request. The rule-version handlers are scoped because theirs genuinely query the
/// database.
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
