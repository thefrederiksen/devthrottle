using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1019 - the card that nothing could remove. Three sessions were spawned, each call returned a
/// session id, and afterwards every tool denied they existed: the fleet listing and
/// `session done` / `interrupt` / `buffer` all reported no such session, while one card stayed on the
/// rail, frozen. Restarting the Director was the only remedy.
///
/// The mechanism is not a lost session - it is a roster that omitted rows the Director was still holding.
/// The fleet roster is what every cc-devthrottle verb resolves an id against BEFORE it sends anything, so
/// a row missing from that one list cannot be named, and a row that cannot be named cannot be reaped. The
/// original omission was a route-level filter that dropped ActivityState.Exited - and a CRASHED session
/// sits in exactly that state (a crash was never modelled as its own state, it lives on Session.Crashed)
/// while being deliberately KEPT in the roster and on the rail (issue #959).
///
/// Remove-the-network-port mission, phase 5: the Director's HTTP roster and /fleet/done routes are gone
/// with the listener, so these tests drive what replaced them - the SAME surfaces the CLI's calls ride
/// today. The roster a caller resolves against is the one the Director PUSHES up the tunnel, built row
/// by row through ControlEndpoints.Map (the host's SnapshotFullSessions does exactly this). The done
/// verb is SessionWriteExecutor's request-deletion, dispatched through the shared SessionCommandExecutor
/// the tunnel uses. The reap machinery on the other side was always willing; only the naming was
/// impossible. These tests prove the naming, then drive the real reap to completion.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GhostSessionReapableTests : IDisposable
{
    private const string DirectorId = "dir-ghost-test";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly SessionManager _sm;

    public GhostSessionReapableTests()
    {
        // Isolate CC_DIRECTOR_ROOT so this Director has NO Gateway configured. That is the point: the
        // ghost must be reapable on a plain standalone Director.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ghost-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _sm = new SessionManager(new AgentOptions())
        {
            // Reap a flagged session on the first sweep instead of 30s later, so the test drives the real
            // reaper rather than a shortened copy of it.
            DeletionGraceMs = 0,
        };
    }

    public void Dispose()
    {
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A backend whose process exit can be driven, so the REAL crash decision runs.</summary>
    private sealed class ExitableBackend : ISessionBackend
    {
        public int ProcessId => 4321;
        public string Status => "Exitable";
        public bool IsRunning => !_exited;
        public bool HasExited => _exited;
        public CircularTerminalBuffer? Buffer => null;
        private bool _exited;

#pragma warning disable CS0067 // required by the interface, unused here
        public event Action<string>? StatusChanged;
#pragma warning restore CS0067
        public event Action<int>? ProcessExited;

        public void RaiseExit(int code)
        {
            _exited = true;
            ProcessExited?.Invoke(code);
        }

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>
    /// Put a session in the live roster and crash it for real - nothing sets Session.Crashed by hand, the
    /// producer decides it from a genuine process exit, so this cannot pass on a fact production never emits.
    /// This is the exact state the report's frozen card was in: held in the store, shown on the rail, no
    /// process behind it.
    /// </summary>
    private Session AdoptCrashedSession()
    {
        var backend = new ExitableBackend();
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();
        _sm.AdoptSession(session);

        session.ApplyTerminalActivityState(ActivityState.Working);
        backend.RaiseExit(1);   // the real producer decides crash-vs-clean

        Assert.True(session.Crashed);                                  // a genuine crash...
        Assert.Equal(ActivityState.Exited, session.ActivityState);      // ...in the state the old filter dropped
        Assert.NotNull(_sm.GetSession(session.Id));                     // ...and still held, per issue #959
        return session;
    }

    /// <summary>The roster exactly as the Director pushes it up the tunnel: every held session, mapped
    /// row by row through the ONE shared mapper. This is what the CLI's fleet listing is built from.</summary>
    private List<SessionDto> PushedRoster()
        => _sm.ListSessions().Select(s => ControlEndpoints.Map(s, DirectorId)).ToList();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Task<DirectorCommandResult> RequestDeletionAsync(string sessionId, string reason)
        => SessionCommandExecutor.DispatchAsync(_sm, DirectorId, new DirectorCommand
        {
            CommandId = "cmd-ghost",
            Verb = "request-deletion",
            SessionId = sessionId,
            PayloadJson = JsonSerializer.Serialize(new { reason }, Json),
        });

    // ===== Omission one: a held row must be nameable =====

    [Fact]
    public void CrashedSession_theDirectorStillHolds_isInThePushedRoster_soTheCliCanNameIt()
    {
        var ghost = AdoptCrashedSession();

        // The whole of the report's second defect is this assertion. It failed before the fix: the roster
        // filtered ActivityState.Exited, so `cc-devthrottle session done <id>` could not resolve an id it
        // could not list and answered "No session matches" for a card plainly visible on the rail.
        Assert.Contains(PushedRoster(),
            s => string.Equals(s.SessionId, ghost.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CrashedSession_isListedAsCrashed_notSilentlyDressedUpAsLive()
    {
        // Listing it must not mean lying about it. The row carries the crash fact, so `session list` shows
        // a dead session as dead rather than implying there is an agent behind it.
        var ghost = AdoptCrashedSession();

        var row = PushedRoster()
            .Single(s => string.Equals(s.SessionId, ghost.Id.ToString(), StringComparison.OrdinalIgnoreCase));

        Assert.True(row.Crashed);
        Assert.Equal("Exited", row.ActivityState);
    }

    // ===== And once nameable, it must actually go away =====

    [Fact]
    public async Task UnknownSession_stillFailsLoud_soListingHeldRowsDidNotWeakenTheGuard()
    {
        // The counterpart: widening the roster must not make the Director accept an id it has never seen.
        // A genuinely unknown target is still a clear not-found, never a silent accept.
        var result = await RequestDeletionAsync(Guid.NewGuid().ToString(), "never existed");

        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }
}
