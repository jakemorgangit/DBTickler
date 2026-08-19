using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace DBTickler.Core.Configuration;

/// <summary>
/// A named, saved configuration: where to connect and what workload to run.
///
/// The plaintext password is never serialised. On Windows it is stored encrypted with DPAPI
/// scoped to the current user, so the file is useless to anyone else on the machine; on any
/// other platform DPAPI does not exist, and the password is simply not saved rather than
/// being written out in the clear.
/// </summary>
public sealed class SessionConfig
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public string SessionName { get; set; } = "Default";

    public string Server { get; set; } = "localhost";
    public string Database { get; set; } = "AdventureWorks2022";
    public bool IntegratedSecurity { get; set; } = true;
    public string Username { get; set; } = "";
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>Never serialised. Populated from <see cref="ProtectedPassword"/> on load.</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>DPAPI-protected password, base64 encoded. Empty on non-Windows platforms.</summary>
    public string ProtectedPassword { get; set; } = "";

    public int VirtualUsers { get; set; } = 16;
    public int BatchRows { get; set; } = 50;
    public int PayloadBytes { get; set; } = 2048;
    public int DurationSeconds { get; set; } = 60;
    public int RampUpSeconds { get; set; } = 5;
    public int ReadPercent { get; set; } = 70;
    public int InsertPercent { get; set; } = 12;
    public int UpdatePercent { get; set; } = 12;
    public int DeletePercent { get; set; } = 6;
    public int ThinkTimeMs { get; set; }
    public bool ThinkTimeJitter { get; set; } = true;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int MaxErrors { get; set; } = 500;
    public bool SafeMode { get; set; } = true;
    public bool ChaosMode { get; set; }
    public bool ChaosBadQueries { get; set; } = true;
    public bool ChaosConcurrency { get; set; } = true;
    public bool ChaosResourceBurners { get; set; } = true;
    public int ChaosIntensityPercent { get; set; } = 25;
    public int? RandomSeed { get; set; }

    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;

    public ConnectionProfile ToConnectionProfile() => new()
    {
        Server = Server,
        Database = Database,
        IntegratedSecurity = IntegratedSecurity,
        Username = Username,
        Password = Password,
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
    };

    public WorkloadProfile ToWorkloadProfile() => new()
    {
        VirtualUsers = VirtualUsers,
        BatchRows = BatchRows,
        PayloadBytes = PayloadBytes,
        DurationSeconds = DurationSeconds,
        RampUpSeconds = RampUpSeconds,
        ReadPercent = ReadPercent,
        InsertPercent = InsertPercent,
        UpdatePercent = UpdatePercent,
        DeletePercent = DeletePercent,
        ThinkTimeMs = ThinkTimeMs,
        ThinkTimeJitter = ThinkTimeJitter,
        CommandTimeoutSeconds = CommandTimeoutSeconds,
        MaxErrors = MaxErrors,
        SafeMode = SafeMode,
        ChaosMode = ChaosMode,
        ChaosBadQueries = ChaosBadQueries,
        ChaosConcurrency = ChaosConcurrency,
        ChaosResourceBurners = ChaosResourceBurners,
        ChaosIntensityPercent = ChaosIntensityPercent,
        RandomSeed = RandomSeed,
    };

    public static SessionConfig From(string name, ConnectionProfile connection, WorkloadProfile workload) => new()
    {
        SessionName = name,
        Server = connection.Server,
        Database = connection.Database,
        IntegratedSecurity = connection.IntegratedSecurity,
        Username = connection.Username,
        Password = connection.Password,
        Encrypt = connection.Encrypt,
        TrustServerCertificate = connection.TrustServerCertificate,
        VirtualUsers = workload.VirtualUsers,
        BatchRows = workload.BatchRows,
        PayloadBytes = workload.PayloadBytes,
        DurationSeconds = workload.DurationSeconds,
        RampUpSeconds = workload.RampUpSeconds,
        ReadPercent = workload.ReadPercent,
        InsertPercent = workload.InsertPercent,
        UpdatePercent = workload.UpdatePercent,
        DeletePercent = workload.DeletePercent,
        ThinkTimeMs = workload.ThinkTimeMs,
        ThinkTimeJitter = workload.ThinkTimeJitter,
        CommandTimeoutSeconds = workload.CommandTimeoutSeconds,
        MaxErrors = workload.MaxErrors,
        SafeMode = workload.SafeMode,
        ChaosMode = workload.ChaosMode,
        ChaosBadQueries = workload.ChaosBadQueries,
        ChaosConcurrency = workload.ChaosConcurrency,
        ChaosResourceBurners = workload.ChaosResourceBurners,
        ChaosIntensityPercent = workload.ChaosIntensityPercent,
        RandomSeed = workload.RandomSeed,
    };

    internal void ProtectPassword()
    {
        if (string.IsNullOrEmpty(Password) || !OperatingSystem.IsWindows())
        {
            ProtectedPassword = "";
            return;
        }

        try
        {
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(Password), optionalEntropy: null, DataProtectionScope.CurrentUser);
            ProtectedPassword = Convert.ToBase64String(encrypted);
        }
        catch (CryptographicException)
        {
            // Better to lose the saved password than to fall back to writing it in plaintext.
            ProtectedPassword = "";
        }
    }

    internal void UnprotectPassword()
    {
        if (string.IsNullOrEmpty(ProtectedPassword) || !OperatingSystem.IsWindows())
        {
            Password = "";
            return;
        }

        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(ProtectedPassword), optionalEntropy: null, DataProtectionScope.CurrentUser);
            Password = Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            // Saved by a different user or on a different machine; the password cannot be
            // recovered and the operator will be asked for it again.
            Password = "";
        }
    }
}
