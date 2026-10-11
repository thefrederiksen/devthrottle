using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A NEW FACT WITH NO SIGNAL IS INVISIBLE, and defect 5's deliverable shipped that way.
///
/// The Gateway resolves a session's role and stamps it onto the owning Director. The fold reads the stamp
/// and correctly suppresses a controlled worker's red to "supporting". Both halves were right, and the
/// desktop rail still showed red - because nothing told it to re-read. The rail re-reads on activity,
/// status, hold, dictation, number and pending-deletion changes; a role arriving was none of those, so the
/// dot stayed red until some unrelated event happened to fire.
///
/// Every mapper test passed throughout, because they READ the fold, and reading is not rendering. That gap
/// - live consumer, correct producer, no notification between them - is this repository's signature
/// failure, and this mission added OnPendingDeletionChanged one commit earlier for exactly this reason.
///
/// So these pin the SIGNAL, not the value: does the fact announce itself, once, on real changes only.
/// Found by independent review of pull request 1598, not by the mission.
/// </summary>
[Collection("ProcessWideState")]
public sealed class GatewayResolvedRoleSignalTests
{
    private static Session NewSession()
    {
        var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: new NullBackend(),
            claudeSessionId: "claude-test",
            activityState: ActivityState.Working,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning();
        return s;
    }

    [Fact]
    public void StampingARole_RaisesTheChange_SoAViewCanReReadTheFold()
    {
        var s = NewSession();
        var heard = new List<string?>();
        s.OnGatewayResolvedRoleChanged += heard.Add;

        s.SetGatewayResolvedRole("Worker");

        // THE DEFECT: without this event the stamp lands, the fold is right, and the rail never re-reads.
        Assert.Equal(new string?[] { "Worker" }, heard);
        Assert.Equal("Worker", s.GatewayResolvedRole);
    }

    [Fact]
    public void ClearingARole_AlsoRaises_BecauseLosingARoleChangesTheColourToo()
    {
        var s = NewSession();
        s.SetGatewayResolvedRole("Worker");
        var heard = new List<string?>();
        s.OnGatewayResolvedRoleChanged += heard.Add;

        s.SetGatewayResolvedRole(null);

        // A worker whose controller went away goes back to standing on its own - and back to red if it
        // needs you. That edge has to reach the rail as much as the arrival did.
        Assert.Equal(new string?[] { null }, heard);
        Assert.Null(s.GatewayResolvedRole);
    }

    [Fact]
    public void ReStampingTheSameRole_IsSilent_SoTheSweepDoesNotChurnTheRail()
    {
        var s = NewSession();
        s.SetGatewayResolvedRole("Worker");
        var heard = new List<string?>();
        s.OnGatewayResolvedRoleChanged += heard.Add;

        // The Gateway re-resolves the whole fleet on every push and re-stamps unchanged roles. If that
        // fired, every push would repaint every row - the fix would trade an invisible fact for a
        // thrashing one.
        s.SetGatewayResolvedRole("Worker");
        s.SetGatewayResolvedRole("Worker");

        Assert.Empty(heard);
    }

    [Fact]
    public void ABlankStamp_IsTreatedAsCleared_NotAsARoleNamedEmpty()
    {
        var s = NewSession();
        s.SetGatewayResolvedRole("Manager");
        var heard = new List<string?>();
        s.OnGatewayResolvedRoleChanged += heard.Add;

        s.SetGatewayResolvedRole("   ");

        Assert.Equal(new string?[] { null }, heard);
        Assert.Null(s.GatewayResolvedRole);
    }

    [Fact]
    public void AHandlerThatThrows_DoesNotBreakTheStamp_TheCacheIsStillWritten()
    {
        var s = NewSession();
        s.OnGatewayResolvedRoleChanged += _ => throw new InvalidOperationException("a view blew up");

        s.SetGatewayResolvedRole("Worker");

        // The Director's cache is the authority for what it was told; a broken subscriber must not
        // silently lose the Gateway's answer.
        Assert.Equal("Worker", s.GatewayResolvedRole);
    }

    /// <summary>A backend that does nothing: these tests are about the role signal, not the process.</summary>
    private sealed class NullBackend : ISessionBackend
    {
        public int ProcessId => 4321;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface, unused here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
