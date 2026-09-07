namespace GroupSplit.Jobs;

public abstract record JobSchedule
{
    private JobSchedule() { }

    public static OnceSchedule Once(DateTimeOffset at) => new(at);

    public static EverySchedule Every(TimeSpan interval, DateTimeOffset firstRun)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        return new(interval, firstRun);
    }

    public static CronSchedule Cron(CronExpression expression, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(timeZone);
        return new(expression, timeZone);
    }

    public static CronSchedule Cron(string expression, TimeZoneInfo timeZone) =>
        Cron(CronExpression.Parse(expression), timeZone);

    public sealed record OnceSchedule : JobSchedule
    {
        public DateTimeOffset At { get; }
        internal OnceSchedule(DateTimeOffset at) => At = at;
    }

    public sealed record EverySchedule : JobSchedule
    {
        public TimeSpan Interval { get; }
        public DateTimeOffset FirstRun { get; }
        internal EverySchedule(TimeSpan interval, DateTimeOffset firstRun) =>
            (Interval, FirstRun) = (interval, firstRun);
    }

    public sealed record CronSchedule : JobSchedule
    {
        public CronExpression Expression { get; }
        public TimeZoneInfo TimeZone { get; }
        internal CronSchedule(CronExpression expression, TimeZoneInfo timeZone) =>
            (Expression, TimeZone) = (expression, timeZone);
    }
}
