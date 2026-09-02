using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Test.Data;

public static class Extensions
{
    private static readonly ConditionalWeakTable<AppHostFixture,
            ConcurrentDictionary<string, object?>>
        AppHostMemberCache = [];

    /// <summary>
    /// This is a solution to text fixtures not being able to depend on others. Ideally, we would have a DbFixture
    /// with an AppHostFixture inside. But that is not supported. So, extensions with cache!!!
    /// </summary>
    extension(AppHostFixture appHost)
    {
        private T GetOrCreate<T>(Func<AppHostFixture, T> factory,
            [CallerMemberName] string methodName = "")
        {
            return appHost.GetOrCreate(factory, appHost, methodName);
        }

        private T GetOrCreate<T, TFactoryArgs>(Func<TFactoryArgs, T> factory,
            TFactoryArgs args, [CallerMemberName] string memberName = "")
        {
            var memberCache = AppHostMemberCache
                .GetOrAdd(appHost, []);

            var methodResult = memberCache.GetOrAdd(memberName,
                static (_, funcWithArgs) => funcWithArgs.factory(funcWithArgs.args), new { factory, args });

            return (T?)methodResult!; // This is very hard to properly annotate with nullability.
        }

        public Task SeedAsync()
            => appHost.GetOrCreate(static async appHost =>
            {
                var finishedTask = appHost.Application.ResourceNotifications.WaitForResourceAsync("seeder",
                    KnownResourceStates.Finished,
                    TestContext.Current.CancellationToken);

                // If seeder is already finished there is no point in trying to start it.
                // WaitFor... normally returns immediately if the resource is in the desired state.
                if (finishedTask.IsCompletedSuccessfully)
                {
                    await finishedTask;
                    return;
                }

                await appHost.Application.ResourceCommands.ExecuteCommandAsync("seeder", "resource-start",
                    TestContext.Current.CancellationToken);

                await finishedTask;
            });

        public Task<IDbContextFactory<AppDbContext>> GetDbContextFactory()
            => appHost.GetOrCreate(static async appHost =>
            {
                var connectionString =
                    await appHost.Application.GetConnectionStringAsync("db",
                        TestContext.Current.CancellationToken);

                var serviceCollection = new ServiceCollection();

                serviceCollection.AddPostgreSqlAppDbContextFactory(connectionString);

                var serviceProvider = serviceCollection.BuildServiceProvider();

                return serviceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            });
    }
}