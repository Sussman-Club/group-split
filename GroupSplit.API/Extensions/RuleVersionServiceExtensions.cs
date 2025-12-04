using GroupSplit.API.Services;
using GroupSplit.API.Services.RuleVersionHandlers;

namespace GroupSplit.API.Extensions;

public static class RuleVersionServiceExtensions
{
    public static IServiceCollection AddRuleVersionServices(this IServiceCollection services)
    {
        // Handlers
        services.AddScoped<PercentRuleVersionHandler>();
        services.AddScoped<PersonalRuleVersionHandler>();

        // Factory
        services.AddScoped<IRuleVersionHandlerFactory, RuleVersionHandlerFactory>();

        return services;
    }
}