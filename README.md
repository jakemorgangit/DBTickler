# DBTickler

**SQL Server workload generator and learning tool**

![DBTickler Demo](https://github.com/jakemorgangit/DBTickler/blob/main/DBTickler.gif)

<sub>The recording above shows v1. The v2 window keeps the same layout and palette but adds
live throughput and latency charts, and tabs for sessions and blocking, wait statistics and
captured deadlocks.</sub>

DBTickler creates realistic activity inside a SQL Server database so you can watch
sessions, waits, locking, blocking and deadlocks happen in real time — and then shows
you what happened. It is built for DBAs, developers, students and performance engineers
who need a repeatable way to bring an idle database to life.

Built with .NET 10. Ships as a WPF desktop app and a command-line tool that shares the
same engine.

---

## What's new in v2

v2 is a rewrite. The source now lives in this repository, the engine was rebuilt around
honest concurrency and real latency measurement, and the observability the tool always
promised is actually in the box.

### The engine

| | v1 | v2 |
|---|---|---|
| Concurrency | "Threads" each fanned out `BatchSize / 20` concurrent statements — 32 threads at batch 1000 opened ~1,600 connections | One statement per virtual user at a time, so the number you set is the concurrency the server sees |
| Latency | Not measured at all | Full percentiles (p50 / p90 / p95 / p99 / p99.9 / max) per operation category, from an HdrHistogram-style histogram |
| Payload generation | Built strings a character at a time — up to ~200,000 appends per row, making the client the bottleneck | Sliced from a pre-generated buffer: one memory copy per row |
| Error budget | Applied per worker, silently multiplying the real threshold by the thread count | A single global count |
| Timing | `DateTime.Now`, so a clock adjustment corrupted the run's duration and throughput | Monotonic `Stopwatch` |
| Stopping | Advertised "immediate stop using KILL", but never set an application name on its connections, so the `KILL` matched nothing | Every connection is stamped `Application Name=DBTickler`, and stop cancels clients *and* kills those sessions server-side |
| Randomness | One `Random` shared across concurrent workers — a data race that can leave it returning a constant | One seeded generator per virtual user |
| End of run | The UI declared the run finished but never cancelled the workers, which kept hammering the server | The run ends when it says it does, with a reported reason |

### New capabilities

- **Works on any database.** v1 hard-coded AdventureWorks object names, so pointing it
  anywhere else produced a 100% error rate with no explanation. v2 probes the target first
  and builds its workload from what is actually there — the AdventureWorks sample schema if
  present, otherwise a generic workload over discovered tables, falling back to system
  catalogue scans on an empty database.
- **Live server-side observability.** Active sessions, blocking chains rendered as trees
  rooted at the head blocker, wait statistics diffed against a baseline taken at the start
  of the run, and deadlock graphs read from the always-on `system_health` session.
- **Deadlocks explained in plain English.** Captured graphs are parsed and narrated —
  which session wanted which lock, who held it, and who was rolled back — instead of
  leaving you to read the XML.
- **Reproducible runs.** Fix the random seed and the same operations run in the same order.
- **Ramp-up.** Virtual users start staggered, so the first seconds measure the server
  rather than connection-pool warm-up.
- **Poisson arrivals.** Think time is drawn from an exponential distribution by default,
  which is what real user populations look like and what makes queueing behaviour visible.
- **Exportable results.** JSON, a CSV summary, and a per-second throughput series, so runs
  can be compared with each other.
- **A production guard.** Before any destructive run, DBTickler checks for Always On,
  clustering, replication, FULL recovery, other applications' connections and
  production-looking names, and asks for confirmation.
- **A command-line tool** for scripted and CI use, with latency and error-rate thresholds
  that set the exit code.
- **Charts, tests and CI.** Live throughput and latency charts in the app, a unit-test
  suite for the engine and analysis code, and a GitHub Actions workflow that builds,
  tests and publishes.

### Safety

DBTickler only ever modifies its own table, `dbo.LoadGen`. Your tables are read from,
never written to. Safe mode is enforced inside the engine rather than by the UI, so a
loaded configuration file cannot smuggle writes past it.

[`docs/OPERATIONS.md`](docs/OPERATIONS.md) lists every statement the tool can issue, and
`dbtickler probe` prints the exact set it would run against a given target.

---

## Who is this for?

- **New DBAs** who need a safe way to see how SQL Server behaves under load
- **Students** exploring locking, blocking, waits and deadlocks
- **Performance engineers** validating monitoring, alerting and dashboards
- **Consultants** demonstrating database behaviour to clients
- **Developers** investigating how their code reacts under concurrency
- **Interview candidates** practising deadlock and blocking demonstrations
- Anyone needing consistent, reproducible load on a non-production SQL Server

---

## Quick start

### Desktop app

1. Download `DBTickler.exe` from the [Releases](https://github.com/jakemorgangit/DBTickler/releases/) page — portable, no installation.
2. Enter your server and database, and press **Test connection**.
3. Press **Setup** to create `dbo.LoadGen` (needed for any write workload).
4. Pick a preset, then press **Start**.

### Command line

```bash
# See what DBTickler makes of a target before running anything
dbtickler probe --server localhost --database AdventureWorks2022

# Read-only load — safe against anything you are allowed to query
dbtickler run --duration 60

# A write workload, with results kept
dbtickler run --profile oltp --unsafe --users 32 --duration 120 --json run.json

# Chaos, reproducibly
dbtickler run --profile chaos --unsafe --duration 60 --seed 42

# Watch blocking as it happens
dbtickler sessions --watch

# Fail a CI job if the server cannot keep up
dbtickler run --profile oltp --unsafe --duration 60 --max-p95 250 --max-error-rate 1
```

Run `dbtickler help` for the full option list. Exit codes: `0` ok, `1` error, `2` bad
usage, `3` cancelled, `4` threshold breached.

Pass the password through `DBTICKLER_PASSWORD` rather than `--password` in scripts, so it
does not land in shell history or CI logs.

---

## Requirements

- **Running the app:** Windows 10 or 11 (64-bit). The published executable is
  self-contained — no .NET installation needed.
- **Running the CLI:** Windows, Linux or macOS.
- **Target:** SQL Server 2016 or later, or Azure SQL. AdventureWorks unlocks the sample
  workload; any other database gets a generic one.
- **Building from source:** .NET 10 SDK. The desktop app builds on Linux and in CI via
  `EnableWindowsTargeting`, but only runs on Windows.

### Permissions

| To do this | You need |
|---|---|
| Read-only workload | `SELECT` on the tables being read |
| Write workload, setup | `CREATE TABLE` in `dbo`, plus `INSERT`/`UPDATE`/`DELETE` on `dbo.LoadGen` |
| Sessions, blocking, waits, deadlock graphs | `VIEW SERVER STATE` |
| Stop with server-side session termination | `ALTER ANY CONNECTION` |

Missing the observability permissions degrades those panels to an explanatory message; it
never stops a run.

---

## Workload presets

| Preset | Users | Mix | What it is for |
|---|---|---|---|
| `readonly` | 16 | 100% read | Safe first run against any database |
| `oltp` | 16 | 70/12/12/6 with think time | A steady, realistic transactional workload |
| `write-heavy` | 32 | 20/45/25/10, large batches | Log flush, lock escalation, page splits |
| `chaos` | 24 | Mixed, chaos at 60% | Monitoring and alert validation |

Every parameter is adjustable from the preset you start with.

### Parameters

| Parameter | Range | What it does |
|---|---|---|
| Virtual users | 1–512 | Concurrent sessions, each running one statement at a time |
| Duration | 0–86,400 s | Run length; 0 runs until you stop it |
| Ramp-up | 0–3,600 s | Window over which users start |
| Batch rows | 1–100,000 | Rows touched per write operation |
| Row payload | 0–8 MB | Generated bytes per written row |
| Think time | 0–600,000 ms | Mean pause between operations, exponentially distributed by default |
| Command timeout | 1–3,600 s | Per-statement timeout |
| Error budget | 0+ | Total errors across all users before the run stops; 0 disables |
| Chaos intensity | 0–100% | Share of operations drawn from the chaos catalogue |
| Seed | any | Fix it to make the run reproducible |

The DML mix must total 100%; the sliders rebalance themselves as you drag them.

---

## Chaos operations

Grouped by what they attack, and built only when the target supports them.

**Bad queries** — cartesian explosion, non-SARGable predicates that wrap the indexed
column in a function, implicit conversions that defeat an index seek.

**Concurrency** — a deadlock trap that takes two row locks in opposite orders on
alternating users (so a cycle reliably forms rather than depending on luck), blocking
chains, lock escalation past the 5,000-lock threshold, and cursor loops.

**Resource burners** — cryptographic hashing for pure CPU, large sorts that request big
memory grants, and tempdb pressure through a materialised intermediate result.

### Manual demonstrations

**Force blocking** holds an exclusive row lock so you can watch other sessions queue
behind it. The wait is client-side, so releasing it is instant.

**Create deadlock** produces a real deadlock between two connections in the same process
and reports which was chosen as the victim. v1 needed you to launch a second copy of the
app and click at the right moment, coordinating through lock files in the temp directory.

---

## Multiple instances

**New window** in the header opens a second copy of the app, each with its own
configuration and metrics — useful for driving several databases or servers at once, or
for putting two different workloads on the same database and watching them contend.

## Sessions

Save named configurations from the header bar. They live in
`%APPDATA%\DBTickler\Sessions\`.

Passwords are protected with DPAPI, scoped to the current Windows user, so the file is
useless to anyone else on the machine. On platforms without DPAPI the password is not
saved at all rather than being written in clear text.

---

## Building from source

```bash
git clone https://github.com/jakemorgangit/DBTickler.git
cd DBTickler

dotnet build
dotnet test

# Desktop app (Windows only at runtime, builds anywhere)
dotnet publish src/DBTickler.App/DBTickler.App.csproj -c Release -r win-x64 --self-contained

# Command-line tool
dotnet publish src/DBTickler.Cli/DBTickler.Cli.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### Layout

```
src/DBTickler.Core/     Engine, metrics, workloads, observability — platform-neutral
src/DBTickler.App/      WPF desktop application (MVVM)
src/DBTickler.Cli/      Command-line tool
tests/                  Unit tests for the core library
```

All the logic lives in `DBTickler.Core`, which has no UI dependency and no dependency on a
live database — the engine talks to an `ISqlSessionFactory` abstraction. That is what lets
the concurrency, ramp-up, cancellation, error-budget and metrics behaviour be tested
without a SQL Server, and it is why the desktop app and the CLI cannot drift apart.

---

## Troubleshooting

**Connection fails.** Check SQL Server is running and reachable on TCP 1433, that the
login has rights to the database, and that the instance name is right. If the server
presents a self-signed certificate, leave *Trust server certificate* ticked.

**"dbo.LoadGen does not exist".** Press **Setup**, or run `dbtickler setup`. Write
workloads need it; read-only runs do not.

**Everything errors immediately.** Run `dbtickler probe` — it reports what DBTickler found
on the target and exactly which operations it would run.

**No waits, sessions or deadlocks shown.** The login needs `VIEW SERVER STATE`. Azure SQL
Database does not expose the `system_health` session, so deadlock graphs are unavailable
there.

**Throughput is lower than expected.** Check the p95 latency and the wait types before
assuming the server is at fault — with think time set, throughput is bounded by
`users / think time`, not by the server.

---

## Contributing

Issues, improvements and feature requests are welcome.

## License

Copyright © 2025 Jake Morgan — Blackcat Data Solutions Limited

## About

DBTickler is developed by **Blackcat Data Solutions Limited** — https://blackcat.wales

For questions, support or consulting, visit our website.

---

> ⚠️ **Warning**
> DBTickler generates real workload and can cause blocking, deadlocks and resource
> pressure. Use it on non-production systems, or with Safe mode enabled.
