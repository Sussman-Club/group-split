using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.PostgreSQL;
using Microsoft.EntityFrameworkCore;

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
                var finished = appHost.Application.ResourceNotifications.WaitForResourceAsync("seeder",
                    KnownResourceStates.Finished,
                    TestContext.Current.CancellationToken);

                // If seeder is already finished there is no point in trying to start it.
                // WaitFor... normally returns immediately if the resource is in the desired state.
                if (finished.IsCompletedSuccessfully)
                {
                    await finished;
                    return;
                }

                // The seeder is registered WithExplicitStart, so it has to be told to run.
                // The command name has to be the one Aspire registers: an unknown name comes
                // back as a failed result rather than an exception, and the wait below then
                // sits on a resource nothing ever started.
                var start = await appHost.Application.ResourceCommands.ExecuteCommandAsync(
                    "seeder",
                    KnownResourceCommands.StartCommand,
                    TestContext.Current.CancellationToken);

                Assert.True(
                    start.Success,
                    $"Starting the seeder failed: {start.Message ?? "no error message"}."
                        + Environment.NewLine + appHost.DescribeResources());

                await appHost.WaitForAsync(
                    "seeder",
                    (notifications, token) => notifications.WaitForResourceAsync(
                        "seeder", KnownResourceStates.Finished, token),
                    $"reach '{KnownResourceStates.Finished}'");
            });

        public async Task<AppDbContext> GetDbContextAsync()
        {
            await appHost.WaitForAsync(
                "db",
                (notifications, token) => notifications.WaitForResourceHealthyAsync("db", token),
                "become healthy");

            var connectionString = await appHost.Application.GetConnectionStringAsync("db",
                TestContext.Current.CancellationToken);
            
            var options = new DbContextOptionsBuilder<PostgreSqlAppDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            
            return new PostgreSqlAppDbContext(options);
        }
    }
}
