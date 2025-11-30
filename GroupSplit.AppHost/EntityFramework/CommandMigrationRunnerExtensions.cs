using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost.EntityFramework;

internal static class CommandMigrationRunnerExtensions
{
    extension(CommandMigrationRunner runner)
    {
        public Task<bool> RunUpdateAsync<TDatabaseResource>(
            IResourceBuilder<TDatabaseResource> db,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
            where TDatabaseResource : IResourceWithConnectionString
        {
            return runner.RunAsync(db, new UpdateDatabaseCommand(), logger, cancellationToken);
        }

        public Task<bool> RunDropAsync<TDatabaseResource>(
            IResourceBuilder<TDatabaseResource> db,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
            where TDatabaseResource : IResourceWithConnectionString
        {
            return runner.RunAsync(db, new DropDatabaseCommand(), logger, cancellationToken);
        }
    }

    private sealed class UpdateDatabaseCommand : IMigrationCommand
    {
        public string Name => "Update";

        public List<string> BuildArguments(string projectPath, string? dbContextTypeName = null)
        {
            var args = new List<string>
            {
                "ef", "database", "update",
                "--no-build",
                "--project", projectPath,
                "--startup-project", projectPath
            };

            if (dbContextTypeName is not null)
            {
                args.Add("--context");
                args.Add(dbContextTypeName);
            }

            return args;
        }
    }

    private sealed class DropDatabaseCommand : IMigrationCommand
    {
        public string Name => "Drop";

        public List<string> BuildArguments(string projectPath, string? dbContextTypeName = null)
        {
            var args = new List<string>
            {
                "ef", "database", "drop",
                "--force",
                "--no-build",
                "--project", projectPath,
                "--startup-project", projectPath
            };

            if (dbContextTypeName is not null)
            {
                args.Add("--context");
                args.Add(dbContextTypeName);
            }

            return args;
        }
    }
}