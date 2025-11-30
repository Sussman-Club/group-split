using System.Collections.Concurrent;

namespace GroupSplit.AppHost.EntityFramework;

internal enum CommandMigrationState
{
    Idle,
    Pending,
    Running,
    Succeeded,
    Failed
}

internal sealed class CommandMigratorRegistry
{
    private readonly ConcurrentDictionary<string, CommandMigrationState> _states = new();

    public CommandMigrationState Get(string dbName)
    {
        return _states.GetValueOrDefault(dbName, CommandMigrationState.Pending);
    }

    public void Set(string dbName, CommandMigrationState state)
    {
        _states[dbName] = state;
    }
}