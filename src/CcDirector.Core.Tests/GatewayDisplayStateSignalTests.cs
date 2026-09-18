using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The Gateway folds each session's display state (colour, label, triage, needs-you-since, snooze clock,
/// snooze-ended) and stamps it down onto the owning Director, which caches it and renders it verbatim. A
/// cached fact with no signal is invisible - the same failure defect 5 shipped with the role - so these pin
/// the SIGNAL: the fact announces itself once, on real changes only, and the cache is authoritative.
///
/// Design: docs/new_architecture/sessions.html.
/// </summary>
public sealed class GatewayDisplayStateSignalTests
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
    public void StampingADisplayState_RaisesTheChange_AndCachesEveryField()
    {
        var s = NewSession();
        var heard = 0;
        s.OnGatewayDisplayStateChanged += () => heard++;

        var since = DateTime.UtcNow.AddMinutes(-11);
        var until = DateTime.UtcNow.AddHours(4);
        s.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", since, until, snoozeExpired: false);

        Assert.Equal(1, heard);
        Assert.Equal("grey", s.GatewayEffectiveColor);
        Assert.Equal("Snoozed", s.GatewayStateLabel);
        Assert.Equal("onHold", s.GatewayTriageBucket);
        Assert.Equal(since, s.GatewayNeedsYouSince);
        Assert.Equal(until, s.GatewaySnoozeUntil);
        Assert.False(s.GatewaySnoozeExpired);
    }

    [Fact]
    public void ReStampingTheSameState_IsSilent_SoTheSweepDoesNotChurnTheRail()
    {
        var s = NewSession();
        s.ApplyGatewayDisplayState("red", "Needs you", "needsYou", null, null, false);
        var heard = 0;
        s.OnGatewayDisplayStateChanged += () => heard++;

        // The Gateway re-folds the whole fleet on every push and on its periodic sweep, re-stamping unchanged
        // answers. If that fired, every sweep would repaint every row.
        s.ApplyGatewayDisplayState("red", "Needs you", "needsYou", null, null, false);
        s.ApplyGatewayDisplayState("red", "Needs you", "needsYou", null, null, false);

        Assert.Equal(0, heard);
    }

    [Fact]
    public void TheInboxLineChangingAlone_RaisesTheChange_AndIsCached()
    {
        var s = NewSession();
        s.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        var heard = 0;
        s.OnGatewayDisplayStateChanged += () => heard++;

        // Message Load mission, slice 4: a message arriving changes nothing but the row line. The row must hear it.
        s.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false, inboxLine: "1 reply waiting");
        Assert.Equal(1, heard);
        Assert.Equal("1 reply waiting", s.GatewayInboxLine);

        s.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false, inboxLine: "1 reply waiting");
        Assert.Equal(1, heard);

        s.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false, inboxLine: null);
        Assert.Equal(2, heard);
        Assert.Null(s.GatewayInboxLine);
    }

    [Fact]
    public void AnyFieldChanging_RaisesTheChange()
    {
        var s = NewSession();
        s.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null, DateTime.UtcNow.AddHours(4), false);
        var heard = 0;
        s.OnGatewayDisplayStateChanged += () => heard++;

        // Only the snooze-ended marker flips; everything else is identical. The row must still be told.
        s.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null, DateTime.UtcNow.AddHours(4), snoozeExpired: true);

        Assert.Equal(1, heard);
        Assert.True(s.GatewaySnoozeExpired);
    }

    [Fact]
    public void ANullColour_ClearsTheStamp_BackToNoAnswer()
    {
        var s = NewSession();
        s.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);
        var heard = 0;
        s.OnGatewayDisplayStateChanged += () => heard++;

        s.ApplyGatewayDisplayState(null, null, null, null, null, false);

        Assert.Equal(1, heard);
        Assert.Null(s.GatewayEffectiveColor);
        Assert.Null(s.GatewayStateLabel);
        Assert.Null(s.GatewayTriageBucket);
    }

    [Fact]
    public void AHandlerThatThrows_DoesNotBreakTheStamp_TheCacheIsStillWritten()
    {
        var s = NewSession();
        s.OnGatewayDisplayStateChanged += () => throw new InvalidOperationException("a view blew up");

        s.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);

        Assert.Equal("blue", s.GatewayEffectiveColor);
    }

    /// <summary>A backend that does nothing: these tests are about the display signal, not the process.</summary>
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
