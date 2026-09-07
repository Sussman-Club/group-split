# Standalone jobs

Supply constructed instances, build once, and dispose the runtime when finished:

```csharp
using GroupSplit.Jobs.Standalone;

var builder = new JobsBuilder();
builder.Handlers.Add<GenerateReport, Report>(new GenerateReportHandler(client));
builder.Handlers.Add<SendEmail>(new SendEmailHandler(mailer));

await using var jobs = builder.Build();
var handle = await jobs.DispatchAsync(new GenerateReport(), cancellationToken);
var report = await handle.GetResultAsync(cancellationToken);
```

Build (or await BuildAsync(cancellationToken)) starts the worker automatically using the same in-memory defaults as AddJobs().
A builder creates one runtime and cannot be modified after building.
Jobs run sequentially on supplied handler instances. An internal host builds the service collection and starts the same hosted worker as the DI API. Handler validation, executor creation, and scoped execution all use the DI builders.

Use Dispatcher.Use(instance) and Receiver.Use(instance) to replace transport components.
Custom dispatcher and receiver implementations must refer to the same transport.

WithoutDefaults() removes the default transport, workers, scheduler, and startup schedule initialization. Supply a dispatcher to
build a dispatch-only runtime; a configured receiver is not consumed in this mode.

Disposal stops the internal host using its normal shutdown timeout, then disposes its service provider. Handlers must cooperate
with cancellation. Pending jobs on the default queue are cancelled; custom
receivers own their pending-delivery cleanup. Supplied instances remain caller-owned
and are never disposed by the runtime. Completion exposes processing-loop failures.

Handles can be awaited repeatedly. Cancelling a wait leaves execution running.
In-memory jobs and results do not survive a process restart.


The standalone subbuilders accept constructed instances and delegate to the existing DI builders.

## Scheduling

The same runtime also implements IJobScheduler:

```csharp
var scheduled = await jobs.ScheduleAsync(
    new GenerateReport(),
    JobSchedule.Once(DateTimeOffset.UtcNow.AddHours(1)),
    cancellationToken);

IReadOnlyList<IScheduledJob> active =
    await jobs.GetScheduledJobsAsync(cancellationToken);

await scheduled.CancelAsync(cancellationToken);
```

Before Build, use builder.Scheduler.Add(job, schedule) with a FirstRun timestamp calculated by the caller. Scheduler.Use(instance)
replaces scheduling with a caller-owned implementation. See ../README.md for
schedule types, cron format, and in-memory execution policies.
