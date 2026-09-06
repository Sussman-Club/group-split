using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
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
/// Singletons, unlike the rule-version handlers: dividing a rule and shaping it for the
/// wire need the rule and nothing else -- no <c>DbContext</c>, no request. The rule-version
/// handlers are scoped because theirs genuinely query the database.
/// </para>
/// </remarks>
public static class SplitRuleServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSplitRuleServices()
        {
            services.AddSplitRuleHandler<EvenSplitRule, EvenSplitRuleDto, EvenSplitRuleHandler>();
            services.AddSplitRuleHandler<PayerSplitRule, PayerSplitRuleDto, PayerSplitRuleHandler>();
            services.AddSplitRuleHandler<PercentSplitRule, PercentSplitRuleDto, PercentSplitRuleHandler>();
            services.AddSplitRuleHandler<SharesSplitRule, SharesSplitRuleDto, SharesSplitRuleHandler>();

            // The dispatcher, which knows no kind by name: it makes the generic interface
            // from the runtime type and asks for it. Registered under both directions,
            // because reading a rule is keyed by the entity and building one by the DTO.
            services.TryAddSingleton<SplitRuleHandler>();
            services.TryAddSingleton<ISplitRuleHandler>(
                provider => provider.GetRequiredService<SplitRuleHandler>());
            services.TryAddSingleton<ISplitRuleFactory>(
                provider => provider.GetRequiredService<SplitRuleHandler>());

            return services;
        }

        /// <summary>
        /// Registers one kind's handler under the entity it reads and the DTO it builds
        /// from, which are the two keys the dispatcher looks it up by.
        /// </summary>
        private IServiceCollection AddSplitRuleHandler<TRule, TDto, THandler>()
            where TRule : SplitRule
            where TDto : SplitRuleDto
            where THandler : class, ISplitRuleHandler<TRule>, ISplitRuleFactory<TDto>
        {
            services.TryAddSingleton<THandler>();

            services.TryAddSingleton<ISplitRuleHandler<TRule>>(
                provider => provider.GetRequiredService<THandler>());

            services.TryAddSingleton<ISplitRuleFactory<TDto>>(
                provider => provider.GetRequiredService<THandler>());

            return services;
        }
    }
}
