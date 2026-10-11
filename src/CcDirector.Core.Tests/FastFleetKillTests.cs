using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Faster STOP (fleet/remote stop path): the FLEET stop escalates to force after the shorter
/// <see cref="AgentOptions.FleetKillGraceMs"/> window, while the LOCAL desktop kill keeps the standard
/// <see cref="AgentOptions.GracefulShutdownTimeoutSeconds"/> window. A spy backend records the graceful
/// window each kill passes, so the behavior is asserted deterministically (no timing).
/// </summary>
[Collection("ProcessWideState")]
public sealed class FastFleetKillTests
{
    // A backend that never spawns a process and records the graceful-shutdown window it was asked to wait.
    private sealed class GraceRecordingBackend : ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Buffer-only";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new CircularTerminalBuffer(4096);
        public int? LastGracefulTimeoutMs { get; private set; }

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer?.Write(data);
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000)
        {
            LastGracefulTimeoutMs = timeoutMs;
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private static (SessionManager sm, Session session, GraceRecordingBackend backend) NewSession(AgentOptions options)
    {
        var sm = new SessionManager(options);
        var backend = new GraceRecordingBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session, backend);
    }

    [Fact]
    public async Task KillSessionAsync_NoOverride_UsesTheStandardDesktopWindow()
    {
        // The LOCAL desktop kill path passes no override, so it stays on GracefulShutdownTimeoutSeconds.
        var (sm, session, backend) = NewSession(new AgentOptions { GracefulShutdownTimeoutSeconds = 5 });
        try
        {
            await sm.KillSessionAsync(session.Id);
            Assert.Equal(5000, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task KillSessionAsync_PositiveOverride_UsesTheOverride()
    {
        var (sm, session, backend) = NewSession(new AgentOptions { GracefulShutdownTimeoutSeconds = 5 });
        try
        {
            await sm.KillSessionAsync(session.Id, gracefulTimeoutMsOverride: 1500);
            Assert.Equal(1500, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task KillSessionAsync_NonPositiveOverride_FallsBackToStandardWindow()
    {
        var (sm, session, backend) = NewSession(new AgentOptions { GracefulShutdownTimeoutSeconds = 3 });
        try
        {
            await sm.KillSessionAsync(session.Id, gracefulTimeoutMsOverride: 0);
            Assert.Equal(3000, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public void FleetKillGraceMs_DefaultsToTheFastWindow()
    {
        using var sm = new SessionManager(new AgentOptions());
        Assert.Equal(1500, sm.FleetKillGraceMs);
    }

    [Fact]
    public void FleetKillGraceMs_HonorsAConfiguredValue()
    {
        using var sm = new SessionManager(new AgentOptions { FleetKillGraceMs = 900 });
        Assert.Equal(900, sm.FleetKillGraceMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void FleetKillGraceMs_DisabledOrNonPositive_FallsBackToStandardWindow(int? disabled)
    {
        using var sm = new SessionManager(new AgentOptions { FleetKillGraceMs = disabled, GracefulShutdownTimeoutSeconds = 4 });
        Assert.Equal(4000, sm.FleetKillGraceMs);
    }
}
