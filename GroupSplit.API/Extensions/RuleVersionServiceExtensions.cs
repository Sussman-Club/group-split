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
            services.AddRuleVersionHandler<PercentRuleVersion, PercentRuleVersionDto, PercentRuleVersionHandler>();
            services.AddRuleVersionHandler<PersonalRuleVersion, PersonalRuleVersionDto, PersonalRuleVersionHandler>(
                ServiceLifetime.Singleton);

            services.AddScoped<IRuleVersionHandler<RuleVersion, RuleVersionDto>, RuleVersionHandler>();

            return services;
        }

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
                typeof(IRuleVersionDetailsHandler<TRuleVersion>),
                sp => sp.GetRequiredService<THandler>(),
                lifetime));

            services.TryAdd(ServiceDescriptor.Describe(
                typeof(IRuleVersionEqualsHandler<TRuleVersionDto>),
                sp => sp.GetRequiredService<THandler>(),
                lifetime));

            return services;
        }
    }
}