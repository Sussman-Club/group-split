# Jobs and scheduling

IJobDispatcher submits work immediately and returns an execution handle.
IJobScheduler registers when work should be submitted and returns an IScheduledJob.
Schedules have no public ID; implementations can keep identity internally.

## Runtime API

```csharp
var scheduled = await scheduler.ScheduleAsync(
    new GenerateReport(),
    JobSchedule.Once(runAt),
    cancellationToken);

var recurring = await scheduler.ScheduleAsync(
    new GenerateReport(),
    JobSchedule.Every(TimeSpan.FromHours(1), firstRun),
    cancellationToken);

var calendar = await scheduler.ScheduleAsync(
    new GenerateReport(),
    JobSchedule.Cron("0 9 * * 1", timeZone),
    cancellationToken);

IReadOnlyList<IScheduledJob> snapshot =
    await scheduler.GetScheduledJobsAsync(cancellationToken);

await scheduled.CancelAsync(cancellationToken);
```

All three operations return ValueTask. Listing is a finite snapshot, not a stream.
Cancelling a registration is idempotent and prevents future dispatches; an occurrence
already being dispatched may still run. Execution cancellation and results belong
to execution handles, not schedule registrations. Jobs with results can be scheduled,
but a schedule does not itself expose those individual results.

Whether completed one-time registrations remain in listings is implementation-specific.

## Configuration

```csharp
services.AddJobs()
    .Scheduler.Add(
        new SweepBankConnections(),
        JobSchedule.Every(
            TimeSpan.FromDays(1),
            DateTimeOffset.Now.AddSeconds(30)));
```

Register handlers as usual. The caller supplies an absolute FirstRun timestamp; host startup does not recalculate it.
Scheduler.Use(instance), Use(factory), or Use<TScheduler>() selects another implementation.
Startup registrations are installed through IJobScheduler, including custom implementations.

The builder family uses the order Jobs, Dispatcher, Handlers, Receiver, Scheduler.
Standalone exposes the same configuration through its instance-based Scheduler subbuilder;
its internal host uses the same DI registrations and workers.

WithoutDefaults removes the in-memory transport, job worker, scheduler worker, default
scheduler, and startup schedule initializer. Custom implementations remain registered.
With defaults disabled, call the custom scheduler yourself or provide its own hosting flow.

## In-memory policies

- Poll due registrations every 100 ms using TimeProvider.
- Dispatch through IJobDispatcher; do not wait for execution before scheduling the next occurrence.
  Occurrences can overlap if the selected transport executes concurrently.
- Every uses an interval between scheduled starts anchored at FirstRun. If time advances
  past several occurrences, dispatch once and continue at the next future boundary.
- Cron uses five-field [Cronos](https://github.com/HangfireIO/Cronos) syntax and its
  time-zone/DST rules. It uses AND when both day-of-month and day-of-week are restricted.
  Seconds and year fields are not accepted.
- Dispatch a past-due Once at the next poll; remove it after successful submission.
  Failed one-time submissions retry after one second. Recurring submissions retry at
  the next scheduled occurrence. Failures are logged.
- Cancellation removes the registration. Stopping the scheduler clears its registrations
  and rejects new scheduling requests.
- The supplied job instance is reused for each occurrence; keep scheduled inputs immutable.
- Schedules and outcomes are process-local and do not survive restart.

Schedules are created with static factories: JobSchedule.Once, Every, and Cron. CronExpression.Parse validates custom expressions; CronExpression.Daily() represents midnight, and Daily(9, 30) represents 09:30. For example: JobSchedule.Cron(CronExpression.Daily(), TimeZoneInfo.Utc). The returned OnceSchedule, EverySchedule, and CronSchedule records expose read-only properties for inspection.
