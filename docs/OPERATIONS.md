# What DBTickler runs on your server

A tool that generates blocking and deadlocks on request should be explicit about what it
executes. This page lists everything DBTickler can issue, and under what conditions.

The authoritative source is
[`src/DBTickler.Core/Workloads/`](../src/DBTickler.Core/Workloads/) — every statement is a
literal in one of those files. `dbtickler probe` prints the exact set that would run
against a given target.

## The one object DBTickler creates

```sql
CREATE TABLE dbo.LoadGen
(
    Id          INT IDENTITY(1,1) NOT NULL,
    Tag         CHAR(16)          NOT NULL,
    TextData    NVARCHAR(MAX)     NULL,
    BinaryData  VARBINARY(MAX)    NULL,
    CreatedAt   DATETIME2(3)      NOT NULL,
    UpdatedAt   DATETIME2(3)      NULL,
    CONSTRAINT PK_LoadGen PRIMARY KEY CLUSTERED (Id)
);
CREATE NONCLUSTERED INDEX IX_LoadGen_Tag ON dbo.LoadGen (Tag) INCLUDE (CreatedAt);
```

**Every write DBTickler performs targets this table and nothing else.** Your tables are
read from, never modified. Setup is idempotent and upgrades a table left by v1 in place.

Ids 1–8 are seeded as anchor rows and are never deleted; the blocking and deadlock
operations depend on them existing.

Remove everything with the **Remove** button or `dbtickler teardown --yes`.

## Reads

Which read set is used depends on what probing finds.

**AdventureWorks** (used only when all seven referenced tables are present): single-order
lookup, order date-range scan, customer order history across four tables, product sales
aggregation, leading-wildcard name search, and a windowed ranking query. All are
parameterised and bounded by `TOP`.

**`dbo.LoadGen`** (whenever the table exists): point lookup, clustered range scan,
nonclustered seek with key lookup, LOB read, and a full-scan aggregate.

**Discovered tables** (any database): for each of the largest user tables, an ordered
`TOP (n)` read, a `COUNT/MIN/MAX` aggregate, and a leading-wildcard search — built from
columns found in `sys.columns`, with identifiers quoted.

**Fallback** (empty database): a join across `sys.all_objects` and `sys.all_columns`, so a
run against a database with no user tables still generates something.

## Writes

Only when safe mode is off, and only when `dbo.LoadGen` exists.

| Operation | Statement shape |
|---|---|
| Insert | Multi-row `INSERT` of `BatchRows` rows with generated payloads |
| Update | `UPDATE TOP (n) dbo.LoadGen … WHERE Id >= @start` |
| Delete | `DELETE TOP (n) FROM dbo.LoadGen WHERE Id > 8` |

## Chaos operations

Off unless chaos mode is enabled, and each category is individually switchable.

### Bad queries

- **Cartesian explosion** — cross join with no predicate, `TOP (50000)`.
- **Non-SARGable predicate** — wraps the indexed column in `SQRT`/`CAST`, so no index can
  be used.
- **Implicit conversion scan** — compares `CHAR` to `NVARCHAR`, defeating the index seek.

### Resource burners

- **CPU burner** — `HASHBYTES('SHA2_512', …)` per row over a cross join.
- **Memory grant hog** — large sort on a wide key with `OPTION (MAXDOP 1)`; may spill.
- **TempDB pressure** — materialises 100,000 rows into a table variable.

### Concurrency attacks

These modify data and are only built when safe mode is off.

- **Deadlock trap** — a transaction updating anchor rows 1 and 2, in an order that reverses
  on alternating virtual users, so a cycle reliably forms.
- **Blocking chain** — holds an exclusive lock on anchor row 3 through a 2–8 second
  `WAITFOR`, then rolls back.
- **Lock escalation** — updates 6,000 rows in one statement, crossing the 5,000-lock
  escalation threshold, then rolls back.
- **Cursor loop** — 200 row-by-row updates through a cursor.

With safe mode **on**, the only concurrency operation built is a read-only one: a
`HOLDLOCK` select held through a short delay, which blocks writers without writing.

## Manual demonstrations

- **Force blocking** — `BEGIN TRAN`, update anchor row 3, hold, roll back. The wait is
  client-side, so releasing is immediate.
- **Create deadlock** — two connections owned by the app take the anchor row locks in
  opposite orders, with a barrier ensuring both hold their first lock before either reaches
  for the second.

## Read-only queries against system views

Used for probing and observability. None of these modify anything.

| Purpose | Views |
|---|---|
| Server identity and version | `SERVERPROPERTY`, `DB_NAME()` |
| Schema discovery | `sys.tables`, `sys.schemas`, `sys.columns`, `sys.types`, `sys.partitions`, `sys.indexes`, `OBJECT_ID` |
| Active sessions and blocking | `sys.dm_exec_sessions`, `sys.dm_exec_requests`, `sys.dm_exec_connections`, `sys.dm_exec_sql_text` |
| Wait statistics | `sys.dm_os_wait_stats` |
| Deadlock graphs | `sys.dm_xe_sessions`, `sys.dm_xe_session_targets` (the `system_health` ring buffer) |
| Production indicators | `sys.databases`, `SERVERPROPERTY('IsHadrEnabled')`, `SERVERPROPERTY('IsClustered')` |

The session listing runs under `READ UNCOMMITTED` so that monitoring never joins the
blocking chain it is trying to display.

## Terminating sessions

Stop cancels the client side and then runs, on the server:

```sql
KILL <spid>;  -- for every session where program_name = 'DBTickler'
```

Each `KILL` is wrapped in `TRY`/`CATCH`, and only sessions this tool opened are matched —
that is why every connection sets `Application Name=DBTickler`.

## Parameterisation

Every value is passed as a bound parameter. The only identifiers interpolated into SQL are
table and column names discovered from the target's own catalogue, and those go through
bracket quoting that doubles embedded `]` (`SqlIdentifier.Quote`).
