# Durable background jobs

`BackgroundJob` is the PostgreSQL outbox for work that must survive an API restart.
The existing `GroupSplit.Jobs` dispatcher and scheduler remain in memory. Persistence
is opt-in through `IBackgroundJobQueue`. A `ProcessBackgroundJobs` job runs
through the existing scheduler, dispatcher and worker. It claims a bounded batch and
invokes registered scoped handlers through `IJobExecutor`. There is no separate
job executor, scheduler or public job-management endpoint. A PostgreSQL notification
listener wakes this same pipeline when committed jobs become pending.

## Register and enqueue

Register an ordinary `IJobHandler<TJob>` through `AddJobs().Handlers.AddJobHandler`.
Register its stable wire name and version in the singleton `BackgroundJobRegistry`:

```csharp
services.AddSingleton(new BackgroundJobRegistry().Add<DeleteFile>("delete-file", 1));
services.AddBackgroundJobs();
services.AddBackgroundJobNotifications(); // PostgreSQL host only; apply the wakeup migration first.
```

Configure the registry before starting the host. Only explicitly registered types can
be deserialized; no CLR type names are read from the database. Retain old contracts
and handlers until their queued rows have drained when introducing a new version.
The durable path supports command jobs; returned results are discarded.

`queue.Enqueue(new DeleteFile(key))` stages a row in the caller's `AppDbContext`.
Call `SaveChangesAsync` with the business change, so either both commit or neither
does. An enqueue without a save does not submit work. No external call should happen
inside that transaction. Payloads must contain only the identifiers necessary for
the handler to reload its inputs; never put access tokens, credentials, personal
export contents or raw provider responses in a job. Manual reconciliation records
follow the same rule. PostgreSQL stores `Payload` as `jsonb`, so malformed JSON cannot
be saved and operators can query its fields. The C# property holds serialized JSON;
the registry translates between it and typed jobs.

## Execution and failures

PostgreSQL triggers send a payload-free `NOTIFY` after a pending job is inserted or
rescheduled. Notifications arrive only after commit; rollbacks do not wake the worker.
The API subscribes with a dedicated connection, coalesces bursts, and dispatches
through the existing executor. It scans once after subscribing or reconnecting to
recover jobs committed while disconnected. Install the migration before the new API.

A once-a-minute safety scan recovers missed notifications, future-dated retries, and
expired leases. Such work may be picked up up to a minute after it becomes due.
Idle safety scans run about 1,440 times per day per API instance, half the former
30-second polling rate (and 97% below the original two-second rate). The listener
uses one additional PostgreSQL connection per instance and reconnects with bounded
backoff. Notifications are hints: the durable table, leases, and fencing remain
authoritative, and multiple instances can receive the same hint safely.

Each invocation processes up to ten jobs, then immediately dispatches a continuation
if the batch was full. This yields to other in-memory work without slowing backlog
draining to ten jobs per safety scan. Each invocation proceeds by
claiming one row at a time with LINQ and a conditional `ExecuteUpdateAsync`. The update
compares the candidate's observed status, attempt count, lease and availability; only
one competing worker can change that state. Losers select again, up to sixteen
conflicts per invocation before yielding to the next scheduled run. Different
instances can work concurrently. This optimistic approach adds database round trips
under contention; it does not provide `SKIP LOCKED` behavior. No database transaction
spans execution. A claim records
a unique lease ID, increments attempts and expires after five minutes. A process
that crashes leaves a row another instance can reclaim when that lease expires.

Delivery is **at least once**. Handlers must be idempotent, including after a crash
between an external side effect and its acknowledgement. The two-minute handler
cancellation budget is cooperative: an unresponsive dependency can outlive it and
the lease. Fencing prevents that old worker from acknowledging a replacement lease;
it cannot undo external side effects or guarantee exactly-once execution. Use this
path for short, repeatable commands; long jobs need lease renewal or a different
transport. Keep instance clocks synchronized.

Failures become pending again with exponential backoff from five seconds up to one
hour. After ten claims, a failed invocation or an expired last lease becomes
`Failed`. Shutdown leaves the lease for crash recovery. Unknown names, versions or
invalid payloads become `AwaitingOperator`. Exception messages and payloads are not
logged or copied into reasons. An outcome is saved in a fresh context, so failed
handlers cannot accidentally save their tracked business changes with it.

Success sets `Completed`, records the finish time and clears the payload. Failed
and manual records retain their minimal inputs for investigation.

## Operator work

`queue.AwaitOperator(type, details, reason)` stages an `AwaitingOperator` row directly.
Use it when credentials are gone or a provider must be reconciled by hand: retrying
such a record would achieve nothing. It is never automatically claimed.

Inspect `Type`, `Version`, `Status`, `Attempts`, `AvailableAt`, `LeaseExpiresAt` and
the safe `Reason` in the database. Correct the cause before retrying failed jobs.
An operator may conditionally change a known executable row from `Failed` or
`AwaitingOperator` to `Pending`, reset `Attempts` to zero, clear lease/finish fields
and set `AvailableAt` to now. Do not reset `Running` rows. Manual reconciliation
types have no handler: resolve their external issue, mark them `Completed`, set
`FinishedAt` and clear `Payload` rather than retrying them.

Operators should purge completed rows after their operational retention window;
failed and awaiting-operator rows require review before removal. Database backups
follow the deployment's separate retention policy.

EF documents conditional updates and checking the affected-row count for concurrency:
[ExecuteUpdate concurrency control](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete#concurrency-control-and-rows-affected).
