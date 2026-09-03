using GroupSplit.Data.Entities;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Services;

public interface ICurrentUser
{
    User User { get; }
}

public interface ICurrentUserInitializer
{
    void Initialize(User user);
}

internal sealed class CurrentUser : ICurrentUser, ICurrentUserInitializer
{
    private User? _user;

    public User User => _user
        ?? throw new InvalidOperationException("The current user has not been initialized for this request.");

    public void Initialize(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (_user is not null)
        {
            throw new InvalidOperationException("The current user has already been initialized for this request.");
        }

        _user = user;
    }
}

public static class CurrentUserServiceCollectionExtensions
{
    public static IServiceCollection AddCurrentUser(this IServiceCollection services)
    {
        services.TryAddScoped<CurrentUser>();
        services.TryAddScoped<ICurrentUser>(provider => provider.GetRequiredService<CurrentUser>());
        services.TryAddScoped<ICurrentUserInitializer>(provider => provider.GetRequiredService<CurrentUser>());
        services.TryAddScoped<IUserProvisioner, UserProvisioner>();
        return services;
    }
}
