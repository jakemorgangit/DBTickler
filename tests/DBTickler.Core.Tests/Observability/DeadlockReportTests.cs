using DBTickler.Core.Observability;

namespace DBTickler.Core.Tests.Observability;

/// <summary>
/// Tests <see cref="DeadlockReport.ParseAll"/> against hand-written XML that mirrors what SQL
/// Server actually emits: the system_health ring buffer's <c>&lt;event&gt;</c>-wrapped form,
/// and the bare <c>&lt;deadlock&gt;</c> form produced by trace flag 1222 / a deadlock graph file.
/// </summary>
public class DeadlockReportTests
{
    // Two processes take two keys in opposite orders: process A (spid 52, the victim) holds
    // key #2 and waits on key #1; process B (spid 57) holds key #1 and waits on key #2.
    private const string WrappedEventXml = """
        <event name="xml_deadlock_report" package="sqlserver" timestamp="2026-01-15T10:30:00.123Z">
          <data name="xml_report">
            <value>
              <deadlock>
                <victim-list>
                  <victimProcess id="process28a9c1e8" />
                </victim-list>
                <process-list>
                  <process id="process28a9c1e8" taskpriority="0" logused="3160" waitresource="KEY: 6:72057594044743680 (1234567890ab)" waittime="4001" lasttranstarted="2026-01-15T10:29:56.020Z" lockMode="X" status="suspended" spid="52" trancount="2" clientapp="DBTickler" hostname="LOADGEN01" loginname="sa" transactionname="user_transaction">
                    <executionStack>
                      <frame procname="adhoc" line="1">UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = 2</frame>
                    </executionStack>
                    <inputbuf>UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = 2;</inputbuf>
                  </process>
                  <process id="process1b7f3d20" taskpriority="0" logused="2896" waitresource="KEY: 6:72057594044743680 (fedcba098765)" waittime="3982" lasttranstarted="2026-01-15T10:29:56.033Z" lockMode="X" status="suspended" spid="57" trancount="2" clientapp="DBTickler" hostname="LOADGEN01" loginname="sa" transactionname="user_transaction">
                    <executionStack>
                      <frame procname="adhoc" line="1">UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = 1</frame>
                    </executionStack>
                    <inputbuf>UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = 1;</inputbuf>
                  </process>
                </process-list>
                <resource-list>
                  <keylock hobtid="72057594044743680" dbid="6" objectname="TestDb.dbo.LoadGen" indexname="PK_LoadGen" mode="X">
                    <owner-list>
                      <owner id="process1b7f3d20" mode="X" />
                    </owner-list>
                    <waiter-list>
                      <waiter id="process28a9c1e8" mode="X" requestType="wait" />
                    </waiter-list>
                  </keylock>
                  <keylock hobtid="72057594044743680" dbid="6" objectname="TestDb.dbo.LoadGen" indexname="PK_LoadGen" mode="X">
                    <owner-list>
                      <owner id="process28a9c1e8" mode="X" />
                    </owner-list>
                    <waiter-list>
                      <waiter id="process1b7f3d20" mode="X" requestType="wait" />
                    </waiter-list>
                  </keylock>
                </resource-list>
              </deadlock>
            </value>
          </data>
        </event>
        """;

    // The bare graph form (no <event> wrapper), as produced by trace flag 1222 or a saved
    // .xdl file. Different session ids from the wrapped fixture, for the fingerprint test.
    private const string BareDeadlockXml = """
        <deadlock>
          <victim-list>
            <victimProcess id="processA1" />
          </victim-list>
          <process-list>
            <process id="processA1" spid="61" lockMode="X" clientapp="DBTickler" hostname="H2" loginname="sa" lasttranstarted="2026-02-01T08:00:00.000Z" transactionname="user_transaction" logused="512">
              <inputbuf>DELETE FROM dbo.LoadGen WHERE Id = 9;</inputbuf>
            </process>
            <process id="processB2" spid="65" lockMode="X" clientapp="DBTickler" hostname="H2" loginname="sa" lasttranstarted="2026-02-01T08:00:00.010Z" transactionname="user_transaction" logused="480">
              <inputbuf>DELETE FROM dbo.LoadGen WHERE Id = 10;</inputbuf>
            </process>
          </process-list>
          <resource-list>
            <keylock objectname="TestDb.dbo.LoadGen" indexname="PK_LoadGen" mode="X">
              <owner-list><owner id="processB2" mode="X" /></owner-list>
              <waiter-list><waiter id="processA1" mode="X" /></waiter-list>
            </keylock>
            <keylock objectname="TestDb.dbo.LoadGen" indexname="PK_LoadGen" mode="X">
              <owner-list><owner id="processA1" mode="X" /></owner-list>
              <waiter-list><waiter id="processB2" mode="X" /></waiter-list>
            </keylock>
          </resource-list>
        </deadlock>
        """;

    [Fact]
    public void Empty_string_returns_an_empty_list()
    {
        Assert.Empty(DeadlockReport.ParseAll(""));
        Assert.Empty(DeadlockReport.ParseAll("   "));
    }

    [Fact]
    public void Null_input_returns_an_empty_list_rather_than_throwing() =>
        Assert.Empty(DeadlockReport.ParseAll(null!));

    [Fact]
    public void Malformed_xml_returns_an_empty_list_rather_than_throwing()
    {
        Assert.Empty(DeadlockReport.ParseAll("this is not xml at all <<< &garbage"));
        Assert.Empty(DeadlockReport.ParseAll("<deadlock><unclosed>"));
    }

    [Fact]
    public void Well_formed_xml_with_no_deadlock_element_returns_an_empty_list() =>
        Assert.Empty(DeadlockReport.ParseAll("<root><somethingElse /></root>"));

    [Fact]
    public void Wrapped_event_form_identifies_the_victim()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));

        var victim = Assert.Single(report.Victims);
        Assert.Equal(52, victim.SessionId);
        Assert.True(victim.IsVictim);

        var survivor = Assert.Single(report.Survivors);
        Assert.Equal(57, survivor.SessionId);
        Assert.False(survivor.IsVictim);
    }

    [Fact]
    public void Wrapped_event_form_parses_process_and_session_details()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));

        Assert.Equal(2, report.Processes.Count);
        Assert.Contains(report.Processes, p => p.SessionId == 52 && p.LoginName == "sa" && p.ClientApplication == "DBTickler");
        Assert.Contains(report.Processes, p => p.SessionId == 57 && p.HostName == "LOADGEN01");
        Assert.True(report.InvolvesDbTickler);
    }

    [Fact]
    public void Wrapped_event_form_reads_the_timestamp_from_the_event_wrapper()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));

        Assert.NotNull(report.Timestamp);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 10, 30, 0, 123, TimeSpan.Zero), report.Timestamp);
    }

    [Fact]
    public void Bare_form_falls_back_to_the_first_processs_lasttranstarted_for_the_timestamp()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(BareDeadlockXml));

        Assert.NotNull(report.Timestamp);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 8, 0, 0, 0, TimeSpan.Zero), report.Timestamp);
    }

    [Fact]
    public void Resources_carry_owners_and_waiters()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));

        Assert.Equal(2, report.Resources.Count);
        foreach (var resource in report.Resources)
        {
            Assert.Equal("keylock", resource.Kind);
            Assert.Equal("TestDb.dbo.LoadGen", resource.ObjectName);
            Assert.Equal("PK_LoadGen", resource.IndexName);
            Assert.Single(resource.Owners);
            Assert.Single(resource.Waiters);
        }

        // The two resources form the classic cross pattern: each process owns one and waits
        // on the other.
        var ownedByVictim = report.Resources.Single(r => r.Owners[0].ProcessId == "process28a9c1e8");
        Assert.Equal("process1b7f3d20", ownedByVictim.Waiters[0].ProcessId);
    }

    [Fact]
    public void Resource_Describe_combines_object_and_index_name()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));
        Assert.All(report.Resources, r => Assert.Equal("keylock on TestDb.dbo.LoadGen.PK_LoadGen", r.Describe()));
    }

    [Fact]
    public void Fingerprint_is_stable_across_repeated_parses_of_identical_xml()
    {
        var first = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));
        var second = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Fingerprint_differs_for_genuinely_different_deadlocks()
    {
        var wrapped = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));
        var bare = Assert.Single(DeadlockReport.ParseAll(BareDeadlockXml));

        Assert.NotEqual(wrapped.Fingerprint, bare.Fingerprint);
    }

    [Fact]
    public void Explain_produces_nonempty_text_mentioning_the_victim_session()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));

        var explanation = report.Explain();

        Assert.False(string.IsNullOrWhiteSpace(explanation));
        Assert.Contains("52", explanation); // the victim's session id
        Assert.Contains("rolled back", explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("57", explanation); // the survivor also appears, as a lock holder
    }

    [Fact]
    public void Explain_mentions_both_lock_waits_in_the_cross_pattern()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));
        var explanation = report.Explain();

        Assert.Contains("Session 52 wanted", explanation);
        Assert.Contains("Session 57 wanted", explanation);
    }

    [Fact]
    public void A_document_with_multiple_deadlock_elements_parses_all_of_them()
    {
        var combined = $"<root>{WrappedEventXml}{BareDeadlockXml}</root>";

        var reports = DeadlockReport.ParseAll(combined);

        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, r => r.Processes.Any(p => p.SessionId == 52));
        Assert.Contains(reports, r => r.Processes.Any(p => p.SessionId == 61));
    }

    [Fact]
    public void Xml_property_round_trips_the_original_deadlock_fragment()
    {
        var report = Assert.Single(DeadlockReport.ParseAll(WrappedEventXml));

        Assert.Contains("<deadlock>", report.Xml);
        Assert.Contains("process28a9c1e8", report.Xml);
    }

    [Fact]
    public void ShortInput_truncates_long_input_buffers()
    {
        var longStatement = new string('x', 500);
        var xml = $"""
            <deadlock>
              <process-list>
                <process id="p1" spid="1"><inputbuf>{longStatement}</inputbuf></process>
              </process-list>
            </deadlock>
            """;

        var report = Assert.Single(DeadlockReport.ParseAll(xml));
        var process = Assert.Single(report.Processes);

        Assert.True(process.ShortInput.Length <= 201);
        Assert.EndsWith("…", process.ShortInput);
    }

    [Fact]
    public void ShortInput_reports_a_placeholder_when_no_statement_was_captured()
    {
        var xml = """
            <deadlock>
              <process-list>
                <process id="p1" spid="1" />
              </process-list>
            </deadlock>
            """;

        var report = Assert.Single(DeadlockReport.ParseAll(xml));
        Assert.Equal("(no statement captured)", Assert.Single(report.Processes).ShortInput);
    }
}
