using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Issue #476: end-to-end wiring of <see cref="TransientErrorAutoResume"/> against a live
/// <see cref="Session"/> with a real terminal buffer. These prove the FULL pipeline the unit
/// tests stub out: the content scan reads the resolved screen grid, and a transient error drives
/// an actual actuation through the <see cref="WingmanActionExecutor"/> chokepoint - recorded in
/// <see cref="Session.RecentWingmanActions"/>. Timings are compressed to keep the test fast; the
/// behavior (arm/continue vs zero) is what is asserted, not the wall-clock spacing (that is the
/// deterministic job of <see cref="AutoResumeLoopTests"/>).
/// </summary>
public sealed class TransientErrorAutoResumeIntegrationTests
{
    // The verbatim field-seen transient error (Screenshot 2026-06-16 133011.png).
    private const string Verbatim500 =
        "API Error: 500 Internal server error. This is a server-side issue, usually temporary - " +
        "try again in a moment. If it persists, check https://status.claude.com";

    private const string InvalidKeyError =
        "API Error: 401 invalid api key - authentication_error";

    // Fast config: first retry and interval well under the test's poll budget.
    private static AutoResumeConfig FastOn() =>
        new(Enabled: true, FirstRetrySeconds: 1, IntervalSeconds: 1, MaxAttempts: 12, MaxElapsedMinutes: 120);

    private static AutoResumeConfig FastOff() => FastOn() with { Enabled = false };

    private static (Session session, BufferOnlyBackend backend) CreateBufferSession(SessionManager manager)
    {
        var backend = new BufferOnlyBackend();
        var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        session.IsBrandNew = false; // a started session, past its first turn
        return (session, backend);
    }

    private static void WriteScreen(BufferOnlyBackend backend, string text)
        => backend.Write(Encoding.UTF8.GetBytes(text + "\r\n"));

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    [Fact]
    public void TransientError_WithSettingOn_AutoContinuesViaExecutor()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var auto = new TransientErrorAutoResume(manager, FastOn);
        try
        {
            auto.Start();
            var (session, backend) = CreateBufferSession(manager);

            // Put the verbatim transient error on screen.
            WriteScreen(backend, Verbatim500);

            // The watcher should detect it and auto-continue at least once via the executor.
            var acted = WaitUntil(() => session.RecentWingmanActions.Count >= 1);

            Assert.True(acted, "Expected at least one auto-continue actuation for a transient error.");
            var first = session.RecentWingmanActions[0];
            Assert.Equal(WingmanAction.ActSubmit, first.Action);
            Assert.Contains("auto-resume", first.Reason);
        }
        finally { auto.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void TransientError_WithSettingOff_ZeroAutoContinues()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var auto = new TransientErrorAutoResume(manager, FastOff);
        try
        {
            auto.Start();
            var (session, backend) = CreateBufferSession(manager);

            WriteScreen(backend, Verbatim500);

            // Give the loop ample time to (not) act. With the setting OFF there must be zero.
            Thread.Sleep(1500);

            Assert.Empty(session.RecentWingmanActions);
        }
        finally { auto.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void NonTransientError_WithSettingOn_ZeroAutoContinues()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var auto = new TransientErrorAutoResume(manager, FastOn);
        try
        {
            auto.Start();
            var (session, backend) = CreateBufferSession(manager);

            // An invalid-key / auth error must NEVER trigger an auto-retry, even ON.
            WriteScreen(backend, InvalidKeyError);

            Thread.Sleep(1500);

            Assert.Empty(session.RecentWingmanActions);
        }
        finally { auto.Dispose(); manager.Dispose(); }
    }
}
