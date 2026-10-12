using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Coverage for the state-change log that backs the Wingman tab: the in-memory ring on
/// <see cref="Session"/> (populated by <c>SetActivityState</c> when the detector calls
/// <c>ApplyTerminalActivityState</c>) and the durable <see cref="StateChangeLog"/>. The
/// detector's timer wiring itself is timing/async and exercised live, not faked here.
///
/// CC_DIRECTOR_ROOT is pinned to a temp dir so the durable log lands there: this test used to
/// append into the REAL running Director's %LOCALAPPDATA%\cc-director\state-changes folder,
/// because StateChangeLog baked that path into a static readonly field no redirect could reach.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class StateChangeLogTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public StateChangeLogTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-statechange-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public void State_changes_are_recorded_newest_first()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

            var recent = session.RecentStateChanges;
            Assert.True(recent.Count >= 2);
            // newest first: the most recent transition is Working -> WaitingForInput
            Assert.Equal(ActivityState.WaitingForInput, recent[0].To);
            Assert.Equal(ActivityState.Working, recent[0].From);
        }
        finally { manager.Dispose(); }
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public void State_change_ring_is_capped_at_100()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            // Alternate so every call is a real transition (same-state writes are no-ops).
            for (int i = 0; i < 130; i++)
                session.ApplyTerminalActivityState(i % 2 == 0 ? ActivityState.Working : ActivityState.WaitingForInput);
            Assert.Equal(100, session.RecentStateChanges.Count);
        }
        finally { manager.Dispose(); }
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public void Recording_a_state_change_raises_the_event()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            var fired = 0;
            session.OnStateChangeRecorded += () => fired++;
            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.True(fired >= 1);
        }
        finally { manager.Dispose(); }
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public void Same_state_write_is_a_no_op_and_records_nothing()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            session.ApplyTerminalActivityState(ActivityState.Working);
            var countAfterFirst = session.RecentStateChanges.Count;
            session.ApplyTerminalActivityState(ActivityState.Working); // identical -> ignored
            Assert.Equal(countAfterFirst, session.RecentStateChanges.Count);
        }
        finally { manager.Dispose(); }
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public void LastOutputAtUtc_is_initialized_not_default()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            // Initialized to construction time, not DateTime default -- the tab subtracts
            // it from "now", so a default would render a nonsense multi-millennium age.
            Assert.True(session.LastOutputAtUtc > DateTime.UtcNow.AddMinutes(-5));
            Assert.True(session.LastOutputAtUtc <= DateTime.UtcNow.AddSeconds(1));
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void StateChangeLog_round_trips_a_record_to_jsonl()
    {
        var sessionId = Guid.NewGuid();
        // Ask CcStorage where the log goes rather than re-deriving the path here: the old copy
        // hardcoded %LOCALAPPDATA% and so silently followed the production class into the real
        // Director's folder. Under the pinned root this resolves inside _root.
        var path = Path.Combine(CcStorage.StateChanges(), sessionId.ToString("N") + ".jsonl");
        var wasEnabled = StateChangeLog.Enabled;
        try
        {
            StateChangeLog.Enabled = true;
            StateChangeLog.Append(sessionId, new StateChangeLog.Record(
                DateTime.UtcNow.ToString("o"), "Working", "WaitingForInput", "red"));

            // Asserted, not assumed: the append landed in this test's throwaway root, never the
            // real Director's data directory.
            Assert.StartsWith(_root, path, StringComparison.OrdinalIgnoreCase);

            Assert.True(File.Exists(path));
            var line = File.ReadAllText(path).Trim();
            Assert.Contains("\"From\":\"Working\"", line);
            Assert.Contains("\"To\":\"WaitingForInput\"", line);
            Assert.Contains("\"Color\":\"red\"", line);
        }
        finally
        {
            StateChangeLog.Enabled = wasEnabled;
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
