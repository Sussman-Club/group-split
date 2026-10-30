using System.Data;

namespace GroupSplit.ServiceDefaults;

/// <summary>Known recurring probes; user-facing queries and job updates remain traceable.</summary>
public static class RoutineDatabaseTelemetry
{
    public static bool IsProbe(IDbCommand command)
    {
        if (command.CommandType != CommandType.Text) return false;
        var sql = command.CommandText.AsSpan().Trim();
        if (sql.Equals("SELECT 1", StringComparison.OrdinalIgnoreCase)) return true;

        // Match the idle job claim SELECT, not job inserts, updates, or other reads.
        return sql.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("FROM \"BackgroundJob\" AS b", StringComparison.Ordinal)
            && sql.Contains("b.\"Status\" = 'Pending'", StringComparison.Ordinal)
            && sql.Contains("b.\"AvailableAt\" <=", StringComparison.Ordinal)
            && sql.Contains("b.\"LeaseExpiresAt\" <=", StringComparison.Ordinal)
            && sql.EndsWith("LIMIT 1", StringComparison.Ordinal);
    }
}
