using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.App.Shared.Services;

public static class ServiceExtensions
{
    public static IServiceCollection AddSharedServices(this IServiceCollection services)
    {
        services.AddMudTheme();
        return services;
    }
}
