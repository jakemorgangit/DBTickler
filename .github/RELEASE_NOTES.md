DBTickler v2 is a rewrite. The source now lives in this repository, the load engine was
rebuilt around measurements that can be trusted, and the server-side observability the tool
always described is finally in the box.

There is also a new command-line tool, `dbtickler-cli.exe`, which shares the same engine as the
desktop app — useful for scripted and CI runs.

## Fixed

Several things in v1 did not do what they said:

- **Stopping a run now actually stops it.** The KILL matched on `program_name = 'DBTickler'`,
  but nothing ever set an application name on the connections, so it matched no sessions at all.
- **The virtual-user count is the real concurrency.** Each v1 "thread" fanned out
  `BatchSize / 20` concurrent statements, so 32 threads at batch size 1000 opened roughly
  1,600 connections — past the default pool limit, which surfaced as timeouts that looked
  like server problems.
- **Reaching the configured duration ends the run.** v1 relabelled the UI as finished while
  its workers carried on working.
- **The error budget is a single global count**, not a per-worker one silently multiplied by
  the thread count.
- **Each virtual user has its own random generator.** One instance was shared across
  concurrent workers, a data race that can leave the generator returning a constant.
- **Run timing uses a monotonic clock**, so a clock adjustment no longer corrupts the
  reported duration and throughput.
- **The client is no longer the bottleneck.** Payloads were built one character at a time —
  around 200,000 append operations per row at large batch sizes. They are now sliced from a
  pre-generated buffer.
- **The log is bounded and batched.** Every statement was previously dispatched to a TextBox
  that was never trimmed, which froze the UI under load and eventually ran out of memory.

## New

- **Latency percentiles** — p50 / p90 / p95 / p99 / p99.9 / max, per operation category.
  v1 measured no latency at all.
- **Works against any database.** The target is probed first and the workload is built from
  what is actually there, so a database that is not AdventureWorks degrades to a generic
  workload instead of failing every operation.
- **Live observability** — active sessions, blocking chains drawn as trees rooted at the head
  blocker, wait statistics diffed against a baseline taken at the start of the run, and
  deadlock graphs read from `system_health`.
- **Deadlocks and waits explained in plain English**, rather than left as XML and acronyms.
- **Live throughput and latency charts.**
- **Reproducible runs** — fix the random seed and the same operations run in the same order.
- **Ramp-up and Poisson-distributed think time.**
- **Exportable results** — JSON, a CSV summary, and a per-second throughput series.
- **A production guard** that checks for Always On, clustering, replication, FULL recovery and
  other applications' connections before any destructive run.
- **A command-line tool** with latency and error-rate thresholds that set the exit code.

## Safety

DBTickler only ever modifies its own table, `dbo.LoadGen`. Your tables are read from, never
written to. Safe mode is enforced inside the engine rather than by the UI, so a loaded
configuration file cannot smuggle writes past it. Every statement the tool can issue is
listed in [docs/OPERATIONS.md](https://github.com/jakemorgangit/DBTickler/blob/main/docs/OPERATIONS.md),
and `dbtickler probe` prints the exact set it would run against a given target.

## Downloads

- **DBTickler.exe** — the desktop app. Portable, self-contained, no .NET installation needed.
- **dbtickler-cli.exe** — the command-line tool.

Both are Windows x64. The core library and CLI are platform-neutral if you build from source.

## Upgrading from v1

Point it at the same database and press **Setup**. The existing `dbo.LoadGen` table is
upgraded in place — it is not dropped and recreated. Saved sessions from v1 are not read by
v2; recreate them from the presets.

## Verification

431 tests run on every push, covering the engine against a fake session factory — including
a direct assertion that concurrency never exceeds the configured virtual-user count — plus a
check that every statement the tool can issue parses against the SQL Server 2022 grammar.
