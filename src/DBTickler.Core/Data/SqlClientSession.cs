using System.Data;
using Microsoft.Data.SqlClient;

namespace DBTickler.Core.Data;

/// <summary>Opens real connections through Microsoft.Data.SqlClient.</summary>
public sealed class SqlClientSessionFactory : ISqlSessionFactory
{
    private readonly string _connectionString;

    public SqlClientSessionFactory(string connectionString, string target)
    {
        _connectionString = connectionString;
        Target = target;
    }

    public string Target { get; }

    public async Task<ISqlSession> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new SqlClientSession(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class SqlClientSession : ISqlSession
{
    private readonly SqlConnection _connection;

    internal SqlClientSession(SqlConnection connection) => _connection = connection;

    public int? SessionId => _connection.ServerProcessId is var spid && spid > 0 ? spid : null;

    public async Task<SqlExecutionResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(request.Sql, _connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = request.TimeoutSeconds,
        };

        foreach (var parameter in request.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);

        if (!request.ReadsResults)
        {
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new SqlExecutionResult(affected, 0);
        }

        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);

        long rowsRead = 0;
        do
        {
            while (rowsRead < request.MaxRows &&
                   await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowsRead++;
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        return new SqlExecutionResult(reader.RecordsAffected, rowsRead);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
