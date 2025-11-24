# DBTickler

SQL Server Stress Tester and Learning Tool

**DBTickler** is a lightweight, practical SQL Server workload generator
designed for DBAs, developers, students, and performance engineers. It
creates realistic activity inside a database so you can observe
sessions, waits, locking, blocking, and deadlocks in real time. Whether
you're learning SQL Server internals or validating monitoring tools,
DBTickler brings an otherwise idle database to life.

Built with .NET 8.0 and WPF, it provides a clean, modern interface with
real-time metrics and configurable workloads that range from gentle
throughput to aggressive chaos scenarios.

## Who Is This For?

DBTickler is especially useful for:

-   **New DBAs** who need a safe way to see how SQL Server behaves under
    load\
-   **Students** or self-learners exploring locking, blocking, waits,
    and deadlocks
-   **Performance engineers** validating monitoring, alerting,
    dashboards, or baselining
-   **Consultants** demonstrating database behaviour to clients\
-   **Developers** investigating how their code reacts under
    concurrency
-   **Interview candidates** practising deadlocks, blocking
    demonstrations, and DMVs
-   **Anyone** needing consistent, reproducible load on a non-production
    SQL Server

It fills the common gap between *theory* and *what SQL Server actually
looks like when something is happening*.

## Overview

DBTickler enables you to:

-   Generate **realistic and configurable workloads**
-   Stress test SQL Server under **controlled or chaotic** conditions
-   Simulate **blocking**, **deadlocks**, **lock escalation**, and
    **resource contention**
-   Observe monitoring tools reacting to **predictable and repeatable
    events**
-   Evaluate SQL Server resilience, stability, and throughput\
-   Use AdventureWorks (recommended) or any target database

## Key Features

### 🎯 Workload Generation

-   Multi-threaded execution (1--128 simulated users)\
-   Configurable DML mix (Read / Insert / Update / Delete)
-   Adjustable batch sizes (1--1000 rows)
-   Optional think time for realistic pacing
-   Duration-based tests (10 seconds to 1 hour)

### 🔒 Safe Mode

A safety feature for demonstrations and production-adjacent
environments:

-   Automatically forces 100% reads
-   Disables all write operations
-   Adjusts sliders and controls to prevent accidental modification

### 💥 Chaos Mode

Purposefully stress SQL Server for resilience testing:

-   **Bad Queries:** CPU burners, Cartesian joins, heavy sorts
-   **Concurrency Attacks:** Blocking chains, deadlock orchestration
-   **Resource Burners:** TempDB pressure, memory-heavy operations

Useful for monitoring validations, alert testing, and training
scenarios.

### 🎮 Manual Attack Tools

-   **Force Blocking** -- Hold locks for 5--60 seconds
-   **Create Deadlock** -- Instantly trigger a deadlock for learning or
    demo purposes

### 💾 Session Management

-   Save named configurations
-   Load and switch between test setups
-   Import/export sessions for reuse across environments

### 📊 Real-time Metrics

-   Operations executed
-   Errors detected
-   Test duration
-   Throughput (ops/sec)
-   Real-time SQL log pane

### 🎨 Clean, Modern UI

-   Light/Dark themes
-   Tooltips for every control
-   Windows 11-style title bar
-   Colour coded status feedback

### ⚡ Performance

-   Immediate stop (using KILL against active sessions)
-   Asynchronous, non-blocking UI updates
-   Graceful thread and task cleanup

## Installation

### Prerequisites

-   Windows 10 or 11 (64-bit)
-   SQL Server 2016 or later
-   AdventureWorks (recommended for full feature coverage)

### Quick Start

1.  Download the latest release from the **Releases** page https://github.com/jakemorgangit/DBTickler/releases/
2.  Run `DBTickler.exe` (portable -- no installation required)

## Usage

### Basic Workflow

1.  **Configure Connection**
    Enter instance name, database, and authentication method.

2.  **Test Connection**
    Confirm SQL connectivity.

3.  **Setup DB Objects (optional)**
    Creates the `dbo.LoadGen` table for write operations.

4.  **Configure Workload Parameters**
    Threads, batch size, duration, think time, max errors.

5.  **Set DML Mix**
    Adjust reads/writes or enable Safe Mode.

6.  **Advanced Options**
    Toggle Safe Mode or Chaos Mode as needed.

7.  **Start Test**
    Watch real-time metrics, then stop when ready.

### Chaos Mode

Enable the Chaos Mode checkbox to unlock stress tests:

-   Bad query patterns
-   Blocking and deadlock tests
-   High-CPU and high-memory operations

Manual actions:

-   **Force Blocking** -- hold locks
-   **Create Deadlock** -- generate immediate deadlock

### Session Management

**Save sessions:**\
Click the save icon, enter a name, stored under:

    %APPDATA%\DBTickler\Sessions\

**Load sessions:**\
Select from dropdown or browse manually.

### Multi-Instance Testing

Open additional DBTickler windows for: - Multiple databases\
- Multiple SQL Servers
- Varied concurrent workloads

## Configuration Details

### Connection Settings

-   Integrated or SQL authentication
-   30-second timeout
-   Trust server certificate enabled


### Workload Parameters
| Parameter | Range | Default | Description |
|-----------|-------|---------|-------------|
| Threads | 1-128 | 32 | Concurrent worker threads |
| Batch Size | 1-1000 | 100 | Rows per operation |
| Duration | 10-3600s | 60s | Test duration |
| Think Time | 0-1000ms | 0ms | Delay between operations |
| Max Errors | 0-1000 | 50 | Error threshold before stopping |

### DML Mix

Define percentage split for: 
- Reads
- Inserts
- Updates
- Deletes

Total must equal 100%.

## Query Types

### Normal Mode

**Reads:**
- Multi-table joins
- Aggregations
- Window functions
- Wildcard filtering

**Writes:**
- Inserts into `dbo.LoadGen`
- Randomised updates
- Deletes based on row selection

### Chaos Mode

**Bad Reads:**\
- Cartesian joins
- Cryptographic CPU burners
- Heavy sorts and aggregations
- Lock hint overload

**Bad Writes:**
- Cursor loops
- Long transactions
- Deadlock-prone updates
- Blocking patterns

## Troubleshooting

### Connection Problems

-   Ensure SQL Server is running
-   Check firewalls (TCP 1433)
-   Verify permissions
-   Test using SSMS

### Permissions Required

Minimum recommended: - SELECT on referenced tables
- INSERT/UPDATE/DELETE on `dbo.LoadGen`
- VIEW SERVER STATE (for session management)

### High Error Counts

-   Check error log
-   Validate AdventureWorks schema
-   Reduce chaos severity

## Technical Details

-   **Framework:** .NET 8
-   **UI:** WPF
-   **Client:** Microsoft.Data.SqlClient
-   **Async Model:** Task-based
-   **Session Storage:** JSON under `%APPDATA%`

SQL Server features used: - DMVs
- Lock hints
- Isolation level control
- Dynamic SQL
- Session termination

## Contributing

Issues, improvements, and feature requests are welcome.

## License

Copyright © 2025
Jake Morgan -- Blackcat Data Solutions Limited

## About

DBTickler is developed by
**Blackcat Data Solutions Limited**
https://blackcat.wales

For questions, support, or consulting, visit our website.

------------------------------------------------------------------------

⚠️ **Warning:**
DBTickler generates real workload and can cause blocking, deadlocks, and
resource pressure. Always use it in non-production environments unless
Safe Mode is enabled.
