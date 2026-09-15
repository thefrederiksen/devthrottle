using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Work item two of the turn-detection phase: the settled screen is captured on EVERY settle, not
/// only when the shadow evidence producer happens to be wired.
///
/// This lives in the serialised half of the Core tests because it drives the detector's real quiet
/// timer and therefore depends on wall-clock time, which the parallel half forbids. The pure half
/// of the same work item - that the row form and the joined form of the body cannot drift apart -
/// is in <c>TerminalBodyRowsTests</c>, in the project that actually runs at commit time.
/// </summary>
public sealed class TerminalSettledCaptureTests : System.IDisposable
{
    private readonly SessionManager _manager;

    public TerminalSettledCaptureTests()
    {
        _manager = new SessionManager(new AgentOptions
        {
            ClaudePath = TestShell.Path,
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        });
    }

    public void Dispose() => _manager.Dispose();

    [Fact]
    public async Task Settled_rows_are_captured_with_no_activity_producer_wired()
    {
        // The defect this proves fixed: the capture used to sit inside "if the shadow evidence
        // producer is not null". A Director running without one settled with no "before" screen
        // recorded at all, so the content rule would have had nothing to compare the next byte
        // against - and would have opened the turn on the byte, silently, for exactly those
        // sessions. Note the producer passed below is null on purpose; that is the whole test.
        var backend = new BufferOnlyBackend();
        var session = _manager.CreateEmbeddedSession(System.IO.Path.GetTempPath(), null, backend);
        session.IsBrandNew = false;

        using var detector = new TerminalStateDetector(
            _manager, driveState: true, System.TimeSpan.FromMilliseconds(150), activityProducer: null);
        detector.Start();

        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnActivityStateChanged += (oldState, newState) =>
        {
            if (oldState == ActivityState.Working && newState == ActivityState.WaitingForInput)
                settled.TrySetResult(true);
        };

        // A finished turn: one line of answer, then the composer prompt with the cursor on it.
        backend.Write(Encoding.UTF8.GetBytes("The deployment finished successfully\r\n> "));

        Assert.True(await settled.Task.WaitAsync(System.TimeSpan.FromSeconds(5)),
            "the session never settled, so the capture was never reached");

        Assert.True(detector.TryGetSettledBodyRows(session.Id, out var rows),
            "the settled screen was not captured on a settle with no activity producer wired");
        Assert.Contains(rows, r => r.Contains("The deployment finished successfully",
            System.StringComparison.Ordinal));

        // The rows stop at the cursor: the composer line the cursor sits on is not body.
        Assert.DoesNotContain(rows, r => r.TrimStart().StartsWith(">", System.StringComparison.Ordinal));
    }
}
