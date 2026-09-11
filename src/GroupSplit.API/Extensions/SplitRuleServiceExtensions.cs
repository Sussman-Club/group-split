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
/// The seeder reaches them here as it reaches everything else in this project.
/// <para>
/// Singletons: dividing a rule and shaping it for the wire need the rule and nothing else
/// -- no <c>DbContext</c>, no request.
/// </para>
/// </remarks>
public static class SplitRuleServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSplitRuleServices()
        {
            services.AddSplitRuleHandler<EvenSplitRuleVersion, EvenSplitRuleDto, EvenSplitRuleHandler>();
            services.AddSplitRuleHandler<PayerSplitRuleVersion, PayerSplitRuleDto, PayerSplitRuleHandler>();
            services.AddSplitRuleHandler<PercentSplitRuleVersion, PercentSplitRuleDto, PercentSplitRuleHandler>();
            services.AddSplitRuleHandler<SharesSplitRuleVersion, SharesSplitRuleDto, SharesSplitRuleHandler>();

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
        /// Registers one kind's handler under the version entity it reads and the DTO it
        /// builds from, which are the two keys the dispatcher looks it up by.
        /// </summary>
        private IServiceCollection AddSplitRuleHandler<TRule, TDto, THandler>()
            where TRule : SplitRuleVersion
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
