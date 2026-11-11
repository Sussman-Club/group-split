namespace GroupSplit.Jobs;

/// <summary>A validated five-field cron expression. Time zones belong to the schedule.</summary>
public sealed class CronExpression
{
    private readonly Cronos.CronExpression _parsed;
    public string Value { get; }

    private CronExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        _parsed = Cronos.CronExpression.Parse(expression, Cronos.CronFormat.Standard);
        Value = expression;
    }

    public static CronExpression Parse(string expression) => new(expression);

    public static CronExpression Daily(int hour = 0, int minute = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hour);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour, 23);
        ArgumentOutOfRangeException.ThrowIfNegative(minute);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minute, 59);
        return Parse(FormattableString.Invariant($"{minute} {hour} * * *"));
    }

    internal DateTimeOffset? GetNextOccurrence(DateTimeOffset from, TimeZoneInfo timeZone) =>
        _parsed.GetNextOccurrence(from, timeZone);

    public override string ToString() => Value;
}
