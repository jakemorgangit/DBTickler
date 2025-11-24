# DBTickler
DBTickler - SQL Server Stress Tester


**DBTickler** is a professional SQL Server stress testing and load generation tool designed for database administrators, developers, and performance engineers. It provides comprehensive workload simulation capabilities, from realistic transaction patterns to aggressive chaos testing scenarios.

## Overview

DBTickler enables you to:
- **Generate realistic database workloads** with configurable transaction mixes
- **Stress test SQL Server instances** under various load conditions
- **Simulate problematic scenarios** with Chaos Mode (blocking, deadlocks, resource contention)
- **Test database resilience** and performance characteristics
- **Validate monitoring and alerting** systems under load

Built with .NET 8.0 and WPF, DBTickler provides a modern, intuitive interface with real-time metrics and advanced configuration options.

## Key Features

### 🎯 Workload Generation
- **Multi-threaded execution** - Simulate concurrent users (1-128 threads)
- **Configurable DML mix** - Set percentages for Read, Insert, Update, and Delete operations
- **Batch size control** - Configure rows per operation (1-1000)
- **Think time** - Add delays between operations to simulate real user behavior
- **Duration-based testing** - Run tests for specific time periods (10s - 1 hour)

### 🔒 Safe Mode
- **Read-only operations** - Automatically sets 100% reads and disables write operations
- **Production-safe testing** - Prevent accidental data modifications
- **Visual feedback** - Sliders automatically adjust when toggled

### 💥 Chaos Mode
Intentionally stressful scenarios to test database resilience:

- **Bad Queries**
  - Cartesian products and table scans
  - CPU-intensive cryptographic functions
  - Memory-intensive sorts and aggregations
  - Suboptimal query patterns

- **Concurrency Attacks**
  - Deliberate blocking scenarios
  - Deadlock orchestration
  - Lock escalation triggers
  - Transaction isolation manipulation

- **Resource Burners**
  - High CPU consumption queries
  - Memory-intensive operations
  - TempDB pressure generators

### 🎮 Manual Attack Features
- **Force Blocking** - Hold locks for configurable duration (5-60 seconds)
- **Create Deadlock** - Trigger instant deadlock scenarios between two sessions

### 💾 Session Management
- **Save configurations** - Store workload parameters as named sessions
- **Load sessions** - Quickly restore previous test configurations
- **Import/Export** - Share session files across environments
- **Session dropdown** - Quick access to saved configurations

### 📊 Real-time Metrics
- **Operations counter** - Total operations executed
- **Error tracking** - Failed operation count
- **Elapsed time** - Test duration
- **Throughput** - Operations per second

### 🎨 Modern UI
- **Dark/Light themes** - Toggle between themes with Windows 11-style title bar integration
- **Comprehensive tooltips** - Hover guidance for all controls
- **Visual feedback** - Color-coded buttons and status indicators
- **SQL logging** - View generated queries in real-time

### ⚡ Performance Features
- **Immediate stop** - Kill all database sessions instantly via KILL command
- **Asynchronous logging** - Non-blocking UI updates
- **Efficient threading** - Proper resource cleanup and cancellation

## Installation

### Prerequisites
- Windows 10/11 (64-bit)
- SQL Server 2016 or later
- AdventureWorks sample database (recommended for testing)

### Quick Start
1. Download the latest release from the [Releases](../../releases) page
2. Run `DBTickler.exe` - no installation required (fully portable)


## Usage

### Basic Workflow

1. **Configure Connection**
   - Server Instance: `localhost` or remote server name
   - Database: Target database name
   - Authentication: Windows (Integrated) or SQL Login

2. **Test Connection**
   - Click "Test Connection" to verify connectivity

3. **Setup Database Objects** (optional)
   - Click "Setup DB Objects" to create the `dbo.LoadGen` table for testing

4. **Configure Workload Parameters**
   - **Threads**: Number of concurrent workers
   - **Batch Size**: Rows per operation
   - **Duration**: Test length in seconds

5. **Set DML Mix**
   - Adjust sliders for Read/Insert/Update/Delete percentages
   - Safe Mode forces 100% reads
   - Unchecking Safe Mode sets balanced 40/20/20/20 mix

6. **Advanced Options**
   - **Safe Mode**: Enable for read-only operations
   - **Think Time**: Add delays between operations (0-1000ms)
   - **Max Errors**: Stop test after N errors
   - **Chaos Mode**: Enable destructive testing scenarios

7. **Start Test**
   - Click "Start" to begin workload generation
   - Monitor real-time metrics (operations, errors, throughput)
   - Click "Stop" to immediately halt all operations

### Chaos Mode Usage

1. Enable "Chaos Mode" checkbox
2. Select desired chaos features:
   - **Bad Queries**: Inefficient query patterns
   - **Concurrency Attacks**: Blocking and deadlocks
   - **Resource Burners**: CPU/Memory intensive operations

3. Use manual attack buttons:
   - **Force Blocking**: Hold lock for specified duration
   - **Create Deadlock**: Trigger immediate deadlock

### Session Management

**Save a Session:**
1. Configure your workload parameters
2. Click the 💾 (Save) button
3. Enter a session name
4. Session saved to `%APPDATA%\DBTickler\Sessions\`

**Load a Session:**
- Select from the dropdown, or
- Click 📂 (Load) to browse for a session file

### Multi-Instance Testing

Click "Launch New Instance" to open additional DBTickler windows for testing:
- Multiple databases simultaneously
- Different servers
- Varied workload patterns

## Configuration Details

### Connection Settings
- **Integrated Security**: Uses Windows Authentication
- **SQL Authentication**: Requires username and password
- **Connection Timeout**: 30 seconds
- **Trust Server Certificate**: Enabled by default

### Workload Parameters
| Parameter | Range | Default | Description |
|-----------|-------|---------|-------------|
| Threads | 1-128 | 32 | Concurrent worker threads |
| Batch Size | 1-1000 | 100 | Rows per operation |
| Duration | 10-3600s | 60s | Test duration |
| Think Time | 0-1000ms | 0ms | Delay between operations |
| Max Errors | 0-1000 | 50 | Error threshold before stopping |

### DML Mix
Configure operation percentages:
- **Reads**: SELECT queries
- **Inserts**: INSERT operations
- **Updates**: UPDATE operations
- **Deletes**: DELETE operations

Total must equal 100%.

## Query Types

### Normal Mode Queries

**Reads:**
- `Sales.SalesOrderHeader` scans with complex joins
- `Person.Person` wildcard searches with aggregations
- `Production.Product` multi-table joins with window functions

**Writes:**
- Inserts to `dbo.LoadGen`
- Updates with random payload generation
- Deletes from `dbo.LoadGen`

### Chaos Mode Queries

**Bad Reads:**
- Cartesian products between large tables
- CPU-intensive `HASHBYTES` operations
- Memory-exhausting sorts and aggregations
- Pessimistic locking hints (`HOLDLOCK`, `TABLOCKX`)

**Bad Writes:**
- Cursor-based loops (hotspot contention)
- Long-running transactions
- Deadlock-prone update patterns
- Blocking attack patterns

## Troubleshooting

### Connection Issues
- Verify SQL Server is running
- Check firewall settings (TCP 1433)
- Ensure login has necessary permissions
- Test with SQL Server Management Studio first

### Permission Requirements
Minimum permissions needed:
- `SELECT` on target tables
- `INSERT`, `UPDATE`, `DELETE` on `dbo.LoadGen` (if Setup DB Objects used)
- `VIEW SERVER STATE` (for session killing feature)

### High Error Counts
- Check Max Errors threshold
- Review log for specific error messages
- Verify database schema matches AdventureWorks
- Reduce chaos mode intensity

## Technical Details

### Architecture
- **Framework**: .NET 8.0
- **UI**: WPF (Windows Presentation Foundation)
- **Database**: Microsoft.Data.SqlClient 5.1.5
- **Threading**: Task-based async/await pattern
- **Logging**: Dispatcher-based async UI updates

### Session Configuration
Sessions stored as JSON in:
```
%APPDATA%\DBTickler\Sessions\
```

### SQL Server Features Used
- Dynamic SQL generation
- System DMVs (`sys.dm_exec_sessions`)
- Transaction isolation levels
- Lock hints and query hints
- Session management (`KILL` command)

## Contributing

Contributions are welcome! Please feel free to submit issues and feature requests

## License

Copyright © 2025 Jake Morgan - Blackcat Data Solutions Limited

## About

**DBTickler** is developed by [Blackcat Data Solutions Limited](https://blackcat.wales)

For questions, support, or consulting services, visit our website.

---

⚠️ **Warning**: DBTickler is a stress testing tool. Chaos Mode can cause significant server load, blocking, and resource contention. Always test in non-production environments first and use Safe Mode when appropriate.


