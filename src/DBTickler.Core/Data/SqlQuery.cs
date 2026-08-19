using System.Data;
using Microsoft.Data.SqlClient;

namespace DBTickler.Core.Data;

/// <summary>
/// Small helper for the read-model queries used by probing and monitoring. These run once
/// per interval rather than once per operation, so clarity matters more here than the
/// allocation-free discipline the load path follows.
/// </summary>
public static class SqlQuery
{
    public static async Task<List<T>> ListAsync<T>(
        string connectionString,
        string sql,
        Func<SqlDataReader, T> map,
        int timeoutSeconds = 15,
        IReadOnlyList<SqlParameterValue>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(map(reader));

        return results;
    }

    public static async Task<T?> ScalarAsync<T>(
        string connectionString,
        string sql,
        int timeoutSeconds = 15,
        IReadOnlyList<SqlParameterValue>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public static async Task ExecuteAsync(
        string connectionString,
        string sql,
        int timeoutSeconds = 60,
        IReadOnlyList<SqlParameterValue>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string? GetNullableString(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static int? GetNullableInt32(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static long GetInt64Loose(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal));
}

/// <summary>Quoting for identifiers that come from the target database rather than from source.</summary>
public static class SqlIdentifier
{
    /// <summary>
    /// Wraps an identifier in brackets, doubling any embedded closing bracket. Discovered
    /// table and column names are interpolated into generated SQL, so they must be quoted
    /// the way QUOTENAME would rather than pasted in raw.
    /// </summary>
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    public static string QuoteTwoPart(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";
}
