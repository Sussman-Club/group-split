using GroupSplit.API.Services.RuleVersionHandlers;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Extensions;

public static class RuleVersionServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddRuleVersionServices()
        {
            // Register all rule-version handlers
            services.AddRuleVersionHandler<PercentRuleVersion, PercentRuleVersionDto, PercentRuleVersionHandler>();
            services.AddRuleVersionHandler<PersonalRuleVersion, PersonalRuleVersionDto, PersonalRuleVersionHandler>(
                ServiceLifetime.Singleton);

            // Register the high-level dispatcher responsible for dynamically resolving
            // the correct rule-version handler at runtime based on the concrete DTO / entity types.
            services.AddScoped<IRuleVersionHandler, RuleVersionHandler>();

            return services;
        }

        /// <summary>
        /// Registers a rule-version handler and automatically maps the handler to all associated interfaces:
        /// - <see cref="IRuleVersionCreateHandler{TRuleVersionDto}"/>
        /// - <see cref="IRuleVersionToDtoHandler{TRuleVersion}"/>
        /// - <see cref="IRuleVersionEqualsHandler{TRuleVersion, TRuleVersionDto}"/>
        /// </summary>
        /// <typeparam name="TRuleVersion">
        /// The concrete rule version entity type.
        /// Must inherit from <see cref="RuleVersion"/>.
        /// </typeparam>
        ///
        /// <typeparam name="TRuleVersionDto">
        /// The DTO type that represents the inbound request data for the rule version.
        /// Must inherit from <see cref="RuleVersionDto"/>.
        /// </typeparam>
        ///
        /// <typeparam name="THandler">
        /// The concrete handler class.
        /// Must implement <see cref="IRuleVersionHandler{TRuleVersion, TRuleVersionDto}"/>.
        /// </typeparam>
        private IServiceCollection AddRuleVersionHandler<TRuleVersion, TRuleVersionDto, THandler>(
            ServiceLifetime lifetime = ServiceLifetime.Scoped)
            where TRuleVersion : RuleVersion
            where TRuleVersionDto : RuleVersionDto
            where THandler : class, IRuleVersionHandler<TRuleVersion, TRuleVersionDto>
        {
            services.TryAdd(ServiceDescriptor.Describe(typeof(THandler), typeof(THandler), lifetime));

            services.TryAdd(ServiceDescriptor.Describe(
                typeof(IRuleVersionCreateHandler<TRuleVersionDto>),
                sp => sp.GetRequiredService<THandler>(),
                lifetime));

            services.TryAdd(ServiceDescriptor.Describe(
                typeof(IRuleVersionToDtoHandler<TRuleVersion>),
                sp => sp.GetRequiredService<THandler>(),
                lifetime));

            services.TryAdd(ServiceDescriptor.Describe(
                typeof(IRuleVersionEqualsHandler<TRuleVersion, TRuleVersionDto>),
                sp => sp.GetRequiredService<THandler>(),
                lifetime));

            return services;
        }
    }
}