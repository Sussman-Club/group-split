# Performance verification

## Evidence and limits

The production error dashboard showed repeated ScrollFeed JSObjectReference disposal errors and merchant-merge transaction/retry errors. Each failed merge also generated multiple API errors because the web HTTP resilience policy retried it.

The day-grouping regression measures API requests for 150 rows in three 50-row windows: 5 requests without a cache, 3 with the bounded page cache. This is an adapter measurement, not a production throughput benchmark. The ledger invalidates its cache on data, filter, group and sort changes. Eight pages are retained per ledger instance.

Production already suppresses routine job/probe database traces, filters EF command logs, exports a restricted metric set and exports metrics every five minutes. The new parent-based 10% trace sampling and HTTP-client Information log filters reduce incoming telemetry before export. Sampling preserves parent decisions across web/API spans; unsampled traces, including errors on those traces, are absent. Warning/Error logs remain available. Set `Telemetry__TraceSampleRatio=1` temporarily for a focused diagnostic run, then restore `0.1`.

The reported 2.58 GB/day and the October 29 spike have not been independently attributed. SSH to `home` and local Docker access were denied in this session. Do not claim the dashboard SQLite files are responsible based on retained row counts or database size alone. No production configuration was changed.

## Attribute writes before further tuning

On the host, take two samples 60 seconds apart of `docker stats --no-stream` and `/proc/<host-pid>/io` for each dashboard process. Get host PIDs with `docker top <dashboard-container> -eo pid,comm`. Record deltas in `write_bytes` and `wchar` (physical writes versus bytes requested). Record request/log/trace/metric counts over the same interval, grouping logs by service and category, traces by source/name and metrics by meter/instrument and tag cardinality. Repeat both at idle and during a representative browse session.

Use `/proc/<pid>/fd` symlink targets to identify open SQLite database, WAL and SHM files, if any. Measure those files and Docker stdout log growth separately. SQLite main-file growth alone misses WAL/checkpoint rewrites; a small retained database can still produce substantial writes. Compare process writes before/after the exporter changes at a similar traffic rate. Dashboard buffers cannot reconstruct a full day's already-evicted ingestion history.

## Database plans

Use a disposable database with the current migrations and realistic data distributions: at least 50 accounts, shared groups of different sizes, and 10k/100k/1m transaction histories over several years. Include long names, settlements and categories. Run the real LINQ listings, counts and summaries, not a simplified table-only query. Keep both All time and This year in the matrix, including deep pages and date/name/amount sorts.

Capture generated SQL and `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` for each query with representative parameters on that local database. Record rows scanned, sort/temp I/O, execution time and index use. If `pg_stat_statements` is already enabled, rank production statements by total execution time and mean execution time; collect normalized statements, not user data. Do not enable extensions or run expensive ANALYZE queries on production for this test.

The model already has `(GroupId, DateTime)`. Candidate `(UserId, DateTime, Id)` or `(GroupId, DateTime, Id)` indexes must be justified by the actual generated query and before/after plans. No speculative index migration is included.

## Mixed API load: 10, 25 and 50 users

Start an isolated local Aspire stack with synthetic accounts/histories and external receipt/AI/bank providers disabled. Supply 50 distinct short-lived test-user bearer tokens in the `GROUPSPLIT_LOAD_IDENTITIES` environment variable as a JSON array of objects with `token` and optional `groupId`. Use synthetic data and an actual local API endpoint; the tool deliberately rejects remote hosts and redirects. Token values never appear in its output. Refresh tokens between runs if they expire.

```powershell
python tools/performance/load.py --base-url http://localhost:<api-port> --seconds 60 --receipt <synthetic-receipt.png>
```

Each user browses up to five transaction pages, browses group activity when configured, creates an expense every fifth cycle, and uploads a receipt on every other create. Uploads do not call transcription; measure transcription separately with the intended fake/local provider. No automatic cleanup runs; discard the isolated stack/database afterward. Do not point this at a production tunnel.

The output records per-operation counts, status codes, p50/p95/p99 and throughput for each stage. Collect PostgreSQL query times, process CPU/working set, container CPU/memory and disk-write deltas simultaneously via the local dashboard and Docker. Use production telemetry settings for the capacity run; collect full traces only during a short query diagnostic run. Capture idle and post-run baselines and let writes settle before comparison. An authentication failure or rate limit is a failed scenario, not successful capacity.

The load runner is prepared; no 10/25/50-user performance result is claimed until the local Docker stack can be run. Browser memory/visible-row virtualization is a separate measurement: ScrollFeed still retains loaded rows. Use a long ledger in a real browser to measure DOM count, heap and filter latency before choosing fixed or variable-height virtualization.
