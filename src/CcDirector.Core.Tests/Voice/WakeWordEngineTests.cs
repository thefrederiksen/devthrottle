using CcDirector.Core.Voice;
using Xunit;

#nullable enable

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Deterministic, audio-free tests for <see cref="WakeWordEngine"/> - the
/// "wingman / wingman send / wingman cancel" state machine. Every test feeds
/// scripted transcript snapshots (the way the realtime provider's cumulative
/// OnPartial does) and asserts the classification events. This is where the
/// grammar's correctness is proven; the desktop dialog is only for live feel.
/// </summary>
public sealed class WakeWordEngineTests
{
    /// <summary>Collects every event the engine raises, for assertions.</summary>
    private sealed class Recorder
    {
        public List<WakeWordEvent> Events { get; } = new();
        public void Attach(WakeWordEngine e) => e.OnEvent += Events.Add;
        public IEnumerable<WakeWordEvent> OfKind(WakeWordEventKind k) => Events.Where(ev => ev.Kind == k);
        public int Count(WakeWordEventKind k) => OfKind(k).Count();

        /// <summary>Last event of a kind, asserting at least one exists (avoids the
        /// forbidden null-forgiving operator at call sites).</summary>
        public WakeWordEvent RequireLast(WakeWordEventKind k)
        {
            var matches = OfKind(k).ToList();
            Assert.NotEmpty(matches);
            return matches[matches.Count - 1];
        }
    }

    private static (WakeWordEngine engine, Recorder rec) NewEngine(string wake = "wingman")
    {
        var engine = new WakeWordEngine(wake);
        var rec = new Recorder();
        rec.Attach(engine);
        return (engine, rec);
    }

    // ===== happy paths =======================================================

    [Fact]
    public void WakeThenSend_OneBreath_CommitsBodyBetween()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("wingman fix the login bug wingman send");
        engine.Flush();

        var committed = rec.RequireLast(WakeWordEventKind.Committed);
        Assert.Equal("fix the login bug", committed.Text);
        Assert.Equal(WakeWordState.Idle, engine.State);
        Assert.True(rec.Count(WakeWordEventKind.WakeDetected) >= 1);
    }

    [Fact]
    public void WakeThenCancel_DiscardsBody_NoCommit()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("wingman delete everything wingman cancel");
        engine.Flush();

        Assert.Equal(1, rec.Count(WakeWordEventKind.Cancelled));
        Assert.Equal(0, rec.Count(WakeWordEventKind.Committed));
        Assert.Equal(WakeWordState.Idle, engine.State);
    }

    [Fact]
    public void Send_HeldUntilFlush_DoesNotCommitEarly()
    {
        // The hold-trailing-token rule: a phrase ending in "send" must not commit
        // until Flush() (the debounce on speech silence) settles it.
        var (engine, rec) = NewEngine();

        engine.Feed("wingman do the thing wingman send");
        Assert.Equal(0, rec.Count(WakeWordEventKind.Committed)); // still held
        Assert.Equal(WakeWordState.Capturing, engine.State);

        engine.Flush();
        Assert.Equal(1, rec.Count(WakeWordEventKind.Committed));
        Assert.Equal("do the thing", rec.RequireLast(WakeWordEventKind.Committed).Text);
    }

    // ===== streaming (cumulative snapshots) ==================================

    [Fact]
    public void StreamingCumulativeSnapshots_ProduceOneCommit()
    {
        var (engine, rec) = NewEngine();

        // Each Feed is the FULL transcript so far, growing - exactly how the
        // realtime provider's OnPartial delivers it.
        engine.Feed("wingman");
        engine.Feed("wingman add");
        engine.Feed("wingman add a");
        engine.Feed("wingman add a test");
        engine.Feed("wingman add a test wingman");
        engine.Feed("wingman add a test wingman send");
        engine.Flush();

        Assert.Equal(1, rec.Count(WakeWordEventKind.Committed));
        Assert.Equal("add a test", rec.RequireLast(WakeWordEventKind.Committed).Text);
        Assert.Equal(WakeWordState.Idle, engine.State);
    }

    [Fact]
    public void BodyUpdated_FiresAsBodyGrows()
    {
        var (engine, _) = NewEngine();
        var bodies = new List<string>();
        engine.OnEvent += ev => { if (ev.Kind == WakeWordEventKind.BodyUpdated) bodies.Add(ev.Text); };

        engine.Feed("wingman one");
        engine.Feed("wingman one two");
        engine.Feed("wingman one two three");
        engine.Flush();

        // Body grows monotonically; final reflects all settled words.
        Assert.NotEmpty(bodies);
        Assert.Equal("one two three", engine.CurrentBody);
    }

    // ===== idle / ignored controls ==========================================

    [Fact]
    public void SendWhileIdle_NoBody_IsControlIgnored()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("wingman send");
        engine.Flush();

        Assert.Equal(1, rec.Count(WakeWordEventKind.ControlIgnored));
        Assert.Equal(0, rec.Count(WakeWordEventKind.Committed));
        Assert.Equal(WakeWordState.Idle, engine.State);
    }

    [Fact]
    public void CancelWhileIdle_IsControlIgnored()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("wingman cancel now");
        engine.Flush();

        Assert.Equal(1, rec.Count(WakeWordEventKind.ControlIgnored));
        Assert.Equal(0, rec.Count(WakeWordEventKind.Cancelled));
    }

    [Fact]
    public void ChatterWhileIdle_IsIgnored_NoEvents()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("so anyway I was thinking about lunch today");
        engine.Flush();

        Assert.Empty(rec.Events);
        Assert.Equal(WakeWordState.Idle, engine.State);
    }

    [Fact]
    public void ChatterThenWake_StartsCaptureAfterWake()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("blah blah blah wingman open the file wingman send");
        engine.Flush();

        Assert.Equal("open the file", rec.RequireLast(WakeWordEventKind.Committed).Text);
    }

    // ===== bare wake =========================================================

    [Fact]
    public void LoneWake_ThenFlush_EntersCapturingWithEmptyBody()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("wingman");
        Assert.Equal(0, rec.Count(WakeWordEventKind.WakeDetected)); // held - might become "wingman send"
        engine.Flush();

        Assert.Equal(1, rec.Count(WakeWordEventKind.WakeDetected));
        Assert.Equal(WakeWordState.Capturing, engine.State);
        Assert.Equal("", engine.CurrentBody);
    }

    // ===== normalization =====================================================

    [Fact]
    public void PunctuationAndCase_AreNormalized()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("Wingman, fix the build. Wingman send.");
        engine.Flush();

        Assert.Equal(1, rec.Count(WakeWordEventKind.Committed));
        // Body is sliced from the raw text (original case/punctuation preserved
        // in the middle), trimmed at the edges.
        Assert.Equal("fix the build.", rec.RequireLast(WakeWordEventKind.Committed).Text);
    }

    // ===== embedded wake word ===============================================

    [Fact]
    public void WingmanFollowedByNonVerb_StaysInBody()
    {
        // "tell the wingman to wait" - "wingman" here is followed by "to", not a
        // control verb, so it must remain part of the body, not commit/cancel.
        var (engine, rec) = NewEngine();

        engine.Feed("wingman tell the wingman to wait wingman send");
        engine.Flush();

        Assert.Equal(1, rec.Count(WakeWordEventKind.Committed));
        Assert.Equal("tell the wingman to wait", rec.RequireLast(WakeWordEventKind.Committed).Text);
    }

    // ===== multiple commands =================================================

    [Fact]
    public void MultipleCommands_InOneTranscript_AreAllProcessed()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("wingman first thing wingman cancel wingman second thing wingman send");
        engine.Flush();

        Assert.Equal(1, rec.Count(WakeWordEventKind.Cancelled));
        Assert.Equal(1, rec.Count(WakeWordEventKind.Committed));
        Assert.Equal("second thing", rec.RequireLast(WakeWordEventKind.Committed).Text);
        Assert.Equal(WakeWordState.Idle, engine.State);
    }

    [Fact]
    public void TwoCommits_BackToBack()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("wingman alpha wingman send wingman beta wingman send");
        engine.Flush();

        Assert.Equal(2, rec.Count(WakeWordEventKind.Committed));
        var commits = rec.OfKind(WakeWordEventKind.Committed).ToList();
        Assert.Equal("alpha", commits[0].Text);
        Assert.Equal("beta", commits[1].Text);
    }

    // ===== custom wake word + reset =========================================

    [Fact]
    public void CustomWakeWord_IsHonored()
    {
        var (engine, rec) = NewEngine(wake: "computer");

        engine.Feed("computer run the tests computer send");
        engine.Flush();

        Assert.Equal("run the tests", rec.RequireLast(WakeWordEventKind.Committed).Text);
    }

    [Fact]
    public void Reset_ClearsStateAndBuffer()
    {
        var (engine, rec) = NewEngine();

        engine.Feed("wingman half a prompt");
        Assert.Equal(WakeWordState.Capturing, engine.State);

        engine.Reset();
        Assert.Equal(WakeWordState.Idle, engine.State);
        Assert.Equal("", engine.CurrentBody);

        // After reset, a fresh cumulative stream (which restarts from short text)
        // is handled as a brand-new session.
        rec.Events.Clear();
        engine.Feed("wingman new prompt wingman send");
        engine.Flush();
        Assert.Equal(1, rec.Count(WakeWordEventKind.Committed));
        Assert.Equal("new prompt", rec.RequireLast(WakeWordEventKind.Committed).Text);
    }

    // ===== constructor guards ===============================================

    [Fact]
    public void Constructor_RejectsEmptyWakeWord()
    {
        Assert.Throws<ArgumentException>(() => new WakeWordEngine("  "));
        Assert.Throws<ArgumentException>(() => new WakeWordEngine("wingman", sendVerb: ""));
    }

    [Fact]
    public void EmptyFeed_NoOp()
    {
        var (engine, rec) = NewEngine();
        engine.Feed("");
        engine.Feed("   ");
        engine.Flush();
        Assert.Empty(rec.Events);
    }
}
