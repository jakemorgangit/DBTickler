using DBTickler.Core.Observability;

namespace DBTickler.Core.Tests.Testing;

/// <summary>Builds <see cref="ActiveRequest"/> values for tests without repeating every required property.</summary>
internal static class ActiveRequestFactory
{
    public static ActiveRequest Create(
        int sessionId,
        int? blockingSessionId = null,
        long waitTimeMs = 0,
        string status = "running",
        string? waitType = "LCK_M_X",
        string command = "SELECT",
        string? programName = "DBTickler") => new()
    {
        SessionId = sessionId,
        BlockingSessionId = blockingSessionId,
        Status = status,
        Command = command,
        WaitType = waitType,
        WaitTimeMs = waitTimeMs,
        WaitResource = null,
        CpuTimeMs = 0,
        LogicalReads = 0,
        ElapsedMs = waitTimeMs,
        LoginName = "test_login",
        HostName = "test_host",
        ProgramName = programName,
        DatabaseName = "TestDb",
        StatementText = "SELECT 1",
    };
}
