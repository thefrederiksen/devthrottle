using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

public sealed class TerminalStateDetectorTests : System.IDisposable
{
    private readonly SessionManager _manager;

    public TerminalStateDetectorTests()
    {
        _manager = new SessionManager(new AgentOptions
        {
            ClaudePath = TestShell.Path,
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        });
    }

    public void Dispose() => _manager.Dispose();

    private Session CreateTestSession() => _manager.CreateSession(System.IO.Path.GetTempPath());


    [Fact]
    public void QuietThreshold_is_a_positive_interval()
    {
        // The detector has two rules (byte -> working; silent QuietThreshold -> needs you) and
        // one tunable: how long the ConPTY must be completely silent before we flag "needs you".
        Assert.True(TerminalStateDetector.QuietThreshold > System.TimeSpan.Zero);
    }

    [Fact]
    public void QuietThreshold_is_ten_seconds()
    {
        // A silence this long flips the session to WaitingForInput (the red "needs you" badge).
        Assert.Equal(System.TimeSpan.FromSeconds(10), TerminalStateDetector.QuietThreshold);
    }

    [Fact]
    public void WingmanLlmThrottle_blocks_a_second_call_within_the_window()
    {
        var sid = System.Guid.NewGuid();
        Assert.True(WingmanLlmThrottle.TryAcquire(sid));    // first call allowed
        Assert.False(WingmanLlmThrottle.TryAcquire(sid));   // immediate second blocked (< 5s)

        var other = System.Guid.NewGuid();
        Assert.True(WingmanLlmThrottle.TryAcquire(other));  // a different session is independent
    }

    [Fact]
    public void SuppressActivityUntilUtc_defaults_to_the_past_so_bytes_count_as_work()
    {
        // With no Director-induced repaint pending, the detector's guard
        // (now < SuppressActivityUntilUtc) is false, so byte activity is counted normally.
        var session = CreateTestSession();
        Assert.True(session.SuppressActivityUntilUtc < System.DateTime.UtcNow);
    }

    [Fact]
    public void SuppressActivityFor_pushes_the_window_into_the_future()
    {
        // A Director-issued resize calls this right before the PTY repaint burst; the detector
        // then ignores byte activity until the window expires.
        var session = CreateTestSession();
        session.SuppressActivityFor(System.TimeSpan.FromSeconds(2));
        Assert.True(session.SuppressActivityUntilUtc > System.DateTime.UtcNow);
    }

    [Fact]
    public void SuppressActivityFor_only_extends_never_shortens()
    {
        // Overlapping resizes (e.g. attach then an immediate layout change) must not cut a
        // longer suppression window short.
        var session = CreateTestSession();
        session.SuppressActivityFor(System.TimeSpan.FromSeconds(10));
        var afterLong = session.SuppressActivityUntilUtc;

        session.SuppressActivityFor(System.TimeSpan.FromMilliseconds(50));
        Assert.Equal(afterLong, session.SuppressActivityUntilUtc);
    }
}
