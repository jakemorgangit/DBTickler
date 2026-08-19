using Microsoft.Data.SqlClient;

namespace DBTickler.Core.Configuration;

/// <summary>
/// Everything needed to reach a SQL Server instance, plus the knobs that decide how
/// DBTickler identifies itself on the server.
/// </summary>
public sealed class ConnectionProfile
{
    /// <summary>
    /// Application name stamped on every connection. The server-side tooling
    /// (session listing, targeted KILL) matches on this exact value, so it must be
    /// applied to every connection the app opens.
    /// </summary>
    public const string ApplicationName = "DBTickler";

    public string Server { get; set; } = "localhost";
    public string Database { get; set; } = "AdventureWorks2022";
    public bool IntegratedSecurity { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Encrypt the connection. Left on by default; SQL Server 2022+ defaults to requiring it.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>
    /// Accept server certificates that do not chain to a trusted root. Convenient for the
    /// lab instances this tool targets, but it disables the check that prevents
    /// man-in-the-middle interception, so it is surfaced in the UI rather than hard-coded.
    /// </summary>
    public bool TrustServerCertificate { get; set; } = true;

    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Upper bound on pooled connections. The engine needs at least one per virtual user;
    /// leaving this at the ADO.NET default of 100 silently caps concurrency and shows up as
    /// bogus "timeout expired" errors that look like server problems but are not.
    /// </summary>
    public int MaxPoolSize { get; set; } = 200;

    public string Describe() => $"{Server}/{Database}";

    /// <summary>
    /// Builds a connection string. <paramref name="maxPoolSize"/> overrides the profile value
    /// when the engine needs headroom for a specific virtual-user count.
    /// </summary>
    public string BuildConnectionString(int? maxPoolSize = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            ApplicationName = ApplicationName,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = Math.Clamp(ConnectTimeoutSeconds, 1, 300),
            Pooling = true,
            MaxPoolSize = Math.Clamp(maxPoolSize ?? MaxPoolSize, 1, 32767),
            MultipleActiveResultSets = false,
        };

        if (IntegratedSecurity)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = Username;
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>Connection string with the password replaced, for logs and exported reports.</summary>
    public string BuildRedactedConnectionString()
    {
        var builder = new SqlConnectionStringBuilder(BuildConnectionString());
        if (!string.IsNullOrEmpty(builder.Password))
            builder.Password = "***";
        return builder.ConnectionString;
    }

    public ValidationResult Validate()
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(Server))
            result.AddError("Server name is required.");
        if (string.IsNullOrWhiteSpace(Database))
            result.AddError("Database name is required.");
        if (!IntegratedSecurity && string.IsNullOrWhiteSpace(Username))
            result.AddError("Username is required when not using Windows authentication.");
        if (ConnectTimeoutSeconds is < 1 or > 300)
            result.AddError("Connect timeout must be between 1 and 300 seconds.");

        if (!Encrypt)
            result.AddWarning("Encryption is disabled — credentials and query text cross the network in clear text.");
        else if (TrustServerCertificate)
            result.AddWarning("Server certificate validation is disabled (TrustServerCertificate=true).");

        return result;
    }

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();
}
