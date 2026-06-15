using System.Text;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// The @-reference submit watchdog (issue #212). The submitting Enter after an @-temp-file
/// reference is unreliable (autocomplete popup eats it; claude's startup window drops Enter
/// keypresses) and a parked prompt freezes the terminal byte count - observed live on the
/// 2026-06-06 restore E2E across several fix attempts. The watchdog judges by output RHYTHM:
/// dead windows get a nudge, settling windows (the reference's own echo/popup repaints -
/// which defeated a one-shot cumulative check live) get patience, real streaming ends the
/// watch.
/// </summary>
public sealed class AtReferenceSubmitVerifierTests
{
    private const string AtRef = "@.temp/input_20260606_170938_53a545.txt";

    /// <summary>Fast beat so the suite stays quick; passed explicitly, no shared state.</summary>
    private static readonly TimeSpan FastBeat = TimeSpan.FromMilliseconds(20);

    private static byte[] Junk(int n) => Encoding.UTF8.GetBytes(new string('x', n));

    [Fact]
    public async Task SubmittedPrompt_StreamsImmediately_NoNudge()
    {
        // Happy path: the first Enter landed; claude streams its response right away
        // (i.e. AFTER the watchdog captured its baseline).
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();

        var watch = AtReferenceSubmitVerifier.EnsureSubmittedAsync(buffer, b => writes.Add(b), AtRef, FastBeat);
        buffer.Write(Junk(4096)); // response stream arrives before the first beat
        await watch;

        Assert.Empty(writes);
    }

    [Fact]
    public async Task EchoThenSilence_IsParked_GetsNudgedUntilStreaming()
    {
        // THE live incident shape that defeated the cumulative check: the typed reference's
        // own echo + popup repaint land first (settling window - no nudge yet), then the
        // TUI freezes (parked). The nudge on the first DEAD window wakes claude up.
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();

        var watch = AtReferenceSubmitVerifier.EnsureSubmittedAsync(buffer, b =>
        {
            writes.Add(b);
            buffer.Write(Junk(4096)); // claude accepts the nudge and streams
        }, AtRef, FastBeat);
        buffer.Write(Junk(700));      // echo + popup repaint: settling, sub-streaming
        await watch;

        var enter = Assert.Single(writes); // beat 1 = settling (wait); beat 2 = dead (nudge)
        Assert.Equal(new byte[] { 0x0D }, enter);
    }

    [Fact]
    public async Task DeadTui_NudgesEveryBeat_ThenGivesUpLoudly()
    {
        // Claude's startup window drops Enters entirely: every beat is dead, every beat
        // nudges, and the watchdog ends with the warning rather than spinning forever.
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();

        await AtReferenceSubmitVerifier.EnsureSubmittedAsync(buffer, b => writes.Add(b), AtRef, FastBeat);

        Assert.Equal(AtReferenceSubmitVerifier.MaxAttempts, writes.Count);
    }

    [Fact]
    public async Task ContinuousSmallActivity_NeverNudges()
    {
        // A slow-thinking claude animates its spinner (small but steady output, above the dead-window
        // threshold every beat). The watchdog must not fire Enters into a session that is visibly alive.
        // Deterministic: the beat hook writes the spinner repaint synchronously, so the test never races
        // a real-time painter against the beat clock (the old flake - a beat could catch an empty window
        // under CI timer jitter and nudge).
        var buffer = new CircularTerminalBuffer(64 * 1024);
        var writes = new List<byte[]>();

        await AtReferenceSubmitVerifier.EnsureSubmittedAsync(
            buffer, b => writes.Add(b), AtRef, FastBeat,
            beatDelay: _ =>
            {
                buffer.Write(Junk(100)); // steady spinner repaint: > QuietWindowBytes, < SubmittedGrowthBytes
                return Task.CompletedTask;
            });

        Assert.Empty(writes);
    }

    [Fact]
    public async Task NullBuffer_SendsOneBlindNudge()
    {
        var writes = new List<byte[]>();
        await AtReferenceSubmitVerifier.EnsureSubmittedAsync(null, b => writes.Add(b), AtRef, FastBeat);
        var enter = Assert.Single(writes);
        Assert.Equal(new byte[] { 0x0D }, enter);
    }
}
