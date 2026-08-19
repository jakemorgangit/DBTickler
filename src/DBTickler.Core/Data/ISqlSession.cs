namespace DBTickler.Core.Data;

/// <summary>
/// A single open connection to the target server, owned by one virtual user for the
/// lifetime of the run.
/// </summary>
public interface ISqlSession : IAsyncDisposable
{
    /// <summary>Server-side session id, when the transport can report one.</summary>
    int? SessionId { get; }

    Task<SqlExecutionResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Opens sessions against a target. The engine depends only on this, which is what lets the
/// whole load pipeline be exercised in tests without a database.
/// </summary>
public interface ISqlSessionFactory
{
    /// <summary>Human-readable target, for logs and reports.</summary>
    string Target { get; }

    Task<ISqlSession> OpenAsync(CancellationToken cancellationToken);
}
