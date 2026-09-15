using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Work item four of the turn-detection phase: a settled session opens a turn because the
/// conversation gained something, not because a byte arrived - behind a switch that ships OFF.
///
/// THE SWITCH-OFF CASE IS THE ONE THAT MATTERS MOST, because that is what every Director will
/// actually run. It is proved twice over: the state writes are identical with the shadow log on
/// and off, and the flip still happens ON THE BYTE rather than after the settling window.
///
/// These drive the detector's real timers, so they live in the serialised half of the Core tests
/// and do NOT run in the default gate. CC_DIRECTOR_ROOT is pinned to a throwaway directory so the
/// shadow log lands there and never in the real running Director's data.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class ContentTurnRuleTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaxDeferral = TimeSpan.FromSeconds(3);

    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly bool _shadowWasEnabled;
    private readonly SessionManager _manager;

    public ContentTurnRuleTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-turnrule-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _shadowWasEnabled = TurnDetectionShadowLog.Enabled;

        _manager = new SessionManager(new AgentOptions
        {
            ClaudePath = TestShell.Path,
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        });
    }

    public void Dispose()
    {
        TurnDetectionShadowLog.Enabled = _shadowWasEnabled;
        _manager.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------------------------------
    // The switch is off: nothing changes
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_the_switch_off_the_shadow_log_changes_no_state_write()
    {
        // The real risk of shipping an observation path is that observing changes the thing
        // observed. So the same scenario is run twice - once with the shadow log off, which is
        // today's code path exactly, and once with it on - and the two transition sequences must
        // be identical.
        TurnDetectionShadowLog.Enabled = false;
        var without = await RunSettleThenRepaint(TurnContentRule.Off);

        TurnDetectionShadowLog.Enabled = true;
        var with = await RunSettleThenRepaint(TurnContentRule.Off);

        Assert.Equal(without, with);

        // And the transitions really are today's: the session woke on the repaint, which is the
        // phantom turn this phase exists to remove and which the switch-off path must still make.
        Assert.Equal(
            new[] { "Working", "WaitingForInput", "Working" },
            without.Take(3).ToArray());
    }

    [Fact]
    public async Task With_the_switch_off_the_flip_still_happens_on_the_byte()
    {
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Off);
        using (detector)
        {
            await SettleWithABody(session, backend);

            // Synchronously after the write, before the settling window could possibly have
            // elapsed. Today's rule is immediate and must stay immediate with the switch off.
            backend.Write(Encoding.UTF8.GetBytes("Update installed - restart to apply\r\n> "));
            Assert.Equal(ActivityState.Working, session.ActivityState);
        }
    }

    [Fact]
    public async Task With_the_switch_off_the_log_still_records_what_the_rule_would_have_done()
    {
        // The pairing the whole phase turns on: the rule is off, so the Director behaves exactly
        // as it does today, AND it writes down the number that decides whether to turn it on.
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Off);
        using (detector)
        {
            await SettleWithABody(session, backend);

            int baseline = CountShadowRows(session.Id);
            backend.Write(Encoding.UTF8.GetBytes("Update installed - restart to apply\r\n> "));
            var row = await WaitForShadowRowAfter(session.Id, baseline);

            Assert.True(row.GetProperty("ByteRuleOpens").GetBoolean());
            Assert.False(row.GetProperty("RowRuleOpens").GetBoolean());
            Assert.Equal("off", row.GetProperty("Rule").GetString());
            Assert.Equal("ClaudeCode", row.GetProperty("Agent").GetString());
            Assert.False(string.IsNullOrEmpty(row.GetProperty("SettledHash").GetString()));
            Assert.False(string.IsNullOrEmpty(row.GetProperty("CurrentHash").GetString()));
        }
    }

    // ------------------------------------------------------------------------------------------
    // The switch is on
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_repaint_that_adds_no_content_holds_the_session_red()
    {
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Row);
        using (detector)
        {
            await SettleWithABody(session, backend);

            // Claude Code's update notice, thirty minutes into an idle session. This is the exact
            // row that ended the owner's snooze between 179 and 325 times a day.
            int baseline = CountShadowRows(session.Id);
            backend.Write(Encoding.UTF8.GetBytes("Update installed - restart to apply\r\n> "));

            await WaitForShadowRowAfter(session.Id, baseline);
            await Task.Delay(SettleDelay);

            Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
        }
    }

    [Fact]
    public async Task Real_output_opens_the_turn()
    {
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Row);
        using (detector)
        {
            await SettleWithABody(session, backend);

            var working = NextTransitionTo(session, ActivityState.Working);
            backend.Write(Encoding.UTF8.GetBytes("All nine call sites now go through one helper.\r\n> "));

            Assert.True(await working.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "a real reply did not open the turn");
        }
    }

    [Fact]
    public async Task A_later_byte_re_arms_a_check_that_found_nothing()
    {
        // The guarantee the claim "a miss can only delay a turn" rests on. Without it, one
        // filtered burst leaves a working session red for good - a LOST turn, not a late one.
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Row);
        using (detector)
        {
            await SettleWithABody(session, backend);

            int baseline = CountShadowRows(session.Id);
            backend.Write(Encoding.UTF8.GetBytes("Update installed - restart to apply\r\n> "));
            await WaitForShadowRowAfter(session.Id, baseline);
            await Task.Delay(SettleDelay);
            Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);

            // The check found nothing and cleared itself. A later byte must ask again.
            var working = NextTransitionTo(session, ActivityState.Working);
            backend.Write(Encoding.UTF8.GetBytes("All nine call sites now go through one helper.\r\n> "));

            Assert.True(await working.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "a byte after a check that found nothing did not arm another check");
        }
    }

    [Fact]
    public async Task A_byte_inside_the_window_pushes_the_check_out_rather_than_being_dropped()
    {
        // Three bursts, each further apart than nothing and each closer together than the window
        // would survive if it were a fixed deadline. If the check were NOT pushed out it would
        // fire after the first burst and the later bursts would each arm their own, giving three
        // rows; pushed out, the whole run is judged once, with every byte counted.
        //
        // Every burst is chrome, so the session never opens and never leaves the settled path.
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Row);
        using (detector)
        {
            await SettleWithABody(session, backend);

            int baseline = CountShadowRows(session.Id);
            long written = 0;
            for (int i = 1; i <= 3; i++)
            {
                var burst = Encoding.UTF8.GetBytes($"Update installed - restart to apply ({i})\r\n> ");
                backend.Write(burst);
                written += burst.Length;
                if (i < 3) await Task.Delay(SettleDelay - TimeSpan.FromMilliseconds(40));
            }

            var row = await WaitForShadowRowAfter(session.Id, baseline);

            Assert.Equal(written, row.GetProperty("Bytes").GetInt64());
            Assert.Equal(baseline + 1, CountShadowRows(session.Id));
            Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
        }
    }

    [Fact]
    public void The_switch_ships_off()
    {
        // Unset, zero, or anything unrecognised is OFF. Turning it on is the owner's decision and
        // he wants the shadow numbers first, so an unrecognised value must never quietly enable it.
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule(null));
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule(""));
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule("0"));
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule("true"));
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule("yes"));

        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule("1"));
        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule("row"));
        Assert.Equal(TurnContentRule.Size, TerminalStateDetector.ResolveContentRule("size"));
    }

    // ------------------------------------------------------------------------------------------
    // A missing baseline is ambiguous, not "nothing changed"
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_settle_that_captured_no_baseline_does_not_lose_a_small_reply()
    {
        // The lost turn the inspection found. The settle path only records a baseline when the
        // screen can be read; a settle with the cursor at the very top records nothing. The check
        // then asked the size rule to compare a real reply against an EMPTY settled side, scored it
        // under the threshold, and held the session red. Nothing arrives afterwards to ask again -
        // that is a turn lost, not delayed, which the whole design forbids.
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Size);
        using (detector)
        {
            // No newline, so the cursor never leaves row zero and no body can be isolated: the
            // session works (an unreadable screen is ambiguous and opens) and then settles with no
            // baseline at all.
            var settled = NextTransitionTo(session, ActivityState.WaitingForInput);
            backend.Write(Encoding.UTF8.GetBytes("working on it"));
            Assert.True(await settled.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "the session never settled");
            Assert.False(detector.TryGetSettledBodyRows(session.Id, out _),
                "this test needs a settle that captured NO baseline");

            // A short real reply - far under the size rule's 200-character threshold, and the only
            // burst there will ever be.
            var working = NextTransitionTo(session, ActivityState.Working);
            backend.Write(Encoding.UTF8.GetBytes("done\r\n> "));

            Assert.True(await working.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "a reply arriving with no settled screen to compare against was lost");
        }
    }

    [Fact]
    public async Task A_settle_that_cannot_read_the_screen_drops_the_previous_turns_baseline()
    {
        // The other half of the same defect: when extraction fails the settle used to leave the
        // PREVIOUS turn's rows in place, so the next check compared a new screen against a stale
        // baseline and called the difference between two unrelated turns "what was gained". An
        // absent baseline is honest; a stale one is a wrong answer with no way to notice.
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Off);
        using (detector)
        {
            await SettleWithABody(session, backend);
            Assert.True(detector.TryGetSettledBodyRows(session.Id, out var first),
                "the first settle should have captured a baseline");
            Assert.NotEmpty(first);

            // Cursor home: the screen keeps its content but the body can no longer be isolated,
            // which is exactly what a repaint that starts at the top of the screen looks like.
            var settledAgain = NextTransitionTo(session, ActivityState.WaitingForInput);
            backend.Write(Encoding.UTF8.GetBytes("\u001b[H"));
            Assert.True(await settledAgain.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "the session never settled a second time");

            Assert.False(detector.TryGetSettledBodyRows(session.Id, out _),
                "a settle that could not read the screen kept the previous turn's baseline");
        }
    }

    // ------------------------------------------------------------------------------------------
    // A check that throws must not swallow the burst or latch the session active
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_check_that_throws_does_not_consume_the_burst()
    {
        // The check cleared the scheduled flag, the pending flag and the byte count BEFORE it read
        // the screen, so anything that threw afterwards consumed the burst. A one-burst reply then
        // has no pending check and no later byte to create one, and the session sits red for good.
        TurnDetectionShadowLog.Enabled = true;
        var rule = new StubRule(opens: true);
        var (session, backend, detector) = Start(TurnContentRule.Row, rowRule: rule);
        using (detector)
        {
            await SettleWithABody(session, backend);

            rule.ThrowOnNextCall = true;
            var working = NextTransitionTo(session, ActivityState.Working);

            // ONE burst, and nothing after it. If the throw ate it, nothing will ever ask again.
            backend.Write(Encoding.UTF8.GetBytes("All nine call sites now go through one helper.\r\n> "));

            Assert.True(await working.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "a check that threw swallowed the burst, so the turn was lost");
        }
    }

    [Fact]
    public async Task A_throw_during_the_state_write_does_not_leave_the_session_stuck_working()
    {
        // The second window. The active latch was set BEFORE the state write, the evidence call and
        // the quiet-timer arm, so a throw in between left the session latched active with no idle
        // countdown running: permanently blue, and every later byte taking the already-active
        // branch that schedules nothing.
        TurnDetectionShadowLog.Enabled = true;
        int throwsLeft = 0;
        var (session, backend, detector) = Start(
            TurnContentRule.Row,
            beforeDetector: (_, newState) =>
            {
                if (newState == ActivityState.Working && Interlocked.Decrement(ref throwsLeft) >= 0)
                    throw new InvalidOperationException("a subscriber faulted during the state write");
            });
        using (detector)
        {
            await SettleWithABody(session, backend);

            Volatile.Write(ref throwsLeft, 1);
            var red = NextTransitionTo(session, ActivityState.WaitingForInput);
            backend.Write(Encoding.UTF8.GetBytes("All nine call sites now go through one helper.\r\n> "));

            Assert.True(await red.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "the session never came back to red, so the failed write left it latched active");
        }
    }

    // ------------------------------------------------------------------------------------------
    // The observation log is appended AFTER the decision it observes
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_shadow_row_is_written_after_the_state_decision_not_before()
    {
        // The append is a synchronous file write under one process-wide lock. Run before the state
        // write it can delay the very decision it is only meant to observe, and there is no upper
        // bound on how long a filesystem write takes. Ordering removes that outright.
        TurnDetectionShadowLog.Enabled = true;
        int rowsSeenDuringTheWrite = -1;
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (session, backend, detector) = Start(TurnContentRule.Row);
        using (detector)
        {
            await SettleWithABody(session, backend);
            int baseline = CountShadowRows(session.Id);

            // The count is taken INSIDE the state write, and the test waits for that observation
            // rather than for the transition - a transition handler and the test thread race, and
            // a test that reads the observation too early passes for the wrong reason.
            session.OnActivityStateChanged += (_, newState) =>
            {
                if (newState != ActivityState.Working) return;
                if (Volatile.Read(ref rowsSeenDuringTheWrite) >= 0) return;
                Volatile.Write(ref rowsSeenDuringTheWrite, CountShadowRows(session.Id));
                observed.TrySetResult(true);
            };

            backend.Write(Encoding.UTF8.GetBytes("All nine call sites now go through one helper.\r\n> "));
            Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)), "the turn never opened");

            Assert.Equal(baseline, Volatile.Read(ref rowsSeenDuringTheWrite));

            // And the row really is written - the ordering must not have silently dropped it.
            await WaitForShadowRowAfter(session.Id, baseline);
        }
    }

    // ------------------------------------------------------------------------------------------
    // An agent that repaints for ever must not cost a check for ever
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_footer_that_repaints_for_ever_costs_no_check_and_no_row()
    {
        // The unbounded cost the inspection found. An agent whose idle terminal never goes
        // byte-silent (Grok) keeps emitting footer frames while it sits settled, and a check armed
        // on RAW BYTES therefore fires at the deferral cap for as long as the session is idle -
        // reading the screen, hashing two bodies and appending a row every few seconds, for ever.
        // The check now rides on a BODY CHANGE, which is both cheaper and the thing worth counting.
        TurnDetectionShadowLog.Enabled = true;
        var (session, backend, detector) = Start(TurnContentRule.Off, agent: AgentKind.Grok);
        using (detector)
        {
            var settled = NextTransitionTo(session, ActivityState.WaitingForInput);
            backend.Write(Encoding.UTF8.GetBytes("The deployment finished successfully\r\n> "));
            Assert.True(await settled.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "the session never settled, so there is nothing to repaint against");

            int baseline = CountShadowRows(session.Id);

            // Three footer frames, spaced wider than the body-check throttle so each one really is
            // looked at and really is found to have changed nothing. The cursor stays on the
            // composer row, so every one of these writes lands at or below it: not body.
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600));
                backend.Write(Encoding.UTF8.GetBytes("\r> thinking " + i + " - esc to interrupt"));
            }

            await Task.Delay(SettleDelay + TimeSpan.FromMilliseconds(400));
            Assert.Equal(baseline, CountShadowRows(session.Id));
            Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);

            // And a real body change is still worth exactly one row.
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            backend.Write(Encoding.UTF8.GetBytes("\r\nAll nine call sites now go through one helper.\r\n> "));
            await WaitForShadowRowAfter(session.Id, baseline);
        }
    }

    // ------------------------------------------------------------------------------------------
    // The cap on deferral, and the switch that reaches the detector
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_that_never_stops_writing_is_judged_at_the_deferral_cap()
    {
        // Nothing reached the cap before: the push-out test's three bursts finish inside a few
        // hundred milliseconds, so REMOVING the cap altogether left it green and the requirement
        // that a chattering agent is eventually judged had no executable guard at all. Here the
        // writing does not stop, and the check has to happen anyway.
        TurnDetectionShadowLog.Enabled = true;
        var cap = TimeSpan.FromMilliseconds(600);
        var (session, backend, detector) = Start(TurnContentRule.Row, maxDeferral: cap);
        using (detector)
        {
            await SettleWithABody(session, backend);
            int baseline = CountShadowRows(session.Id);

            // Every burst is chrome, so the session stays settled and stays on this path. They
            // arrive closer together than the settling window, so each one pushes the check out:
            // without the cap the check is deferred for as long as this loop runs.
            using var stop = new CancellationTokenSource();
            var chatter = Task.Run(async () =>
            {
                int i = 0;
                while (!stop.IsCancellationRequested)
                {
                    backend.Write(Encoding.UTF8.GetBytes($"Update installed - restart to apply ({i++})\r\n> "));
                    await Task.Delay(TimeSpan.FromMilliseconds(80));
                }
            });

            var startedAt = DateTime.UtcNow;
            try
            {
                await WaitForShadowRowAfter(session.Id, baseline);
            }
            finally
            {
                stop.Cancel();
                await chatter;
            }
            var took = DateTime.UtcNow - startedAt;

            Assert.True(took < TimeSpan.FromSeconds(2),
                $"the check was still being deferred after {took.TotalMilliseconds:F0}ms of continuous chatter");
            Assert.True(took >= TimeSpan.FromMilliseconds(400),
                $"the check fired after {took.TotalMilliseconds:F0}ms, which is inside the settling window rather than at the cap");
        }
    }

    [Fact]
    public void The_environment_switch_reaches_the_rule_the_detector_runs()
    {
        // "The switch resolves correctly" was pinned; "the resolved switch is what the detector
        // runs" was not, so a detector that ignored the variable entirely would have passed. This
        // sets the variable and reads back the rule the detector is actually holding.
        var previous = Environment.GetEnvironmentVariable(TerminalStateDetector.ContentRuleVariable);
        try
        {
            Environment.SetEnvironmentVariable(TerminalStateDetector.ContentRuleVariable, "size");
            using (var sized = new TerminalStateDetector(_manager, driveState: false))
                Assert.Equal(TurnContentRule.Size, sized.ContentRule);

            Environment.SetEnvironmentVariable(TerminalStateDetector.ContentRuleVariable, "row");
            using (var rows = new TerminalStateDetector(_manager, driveState: false))
                Assert.Equal(TurnContentRule.Row, rows.ContentRule);

            // And the shipped default, which is the one every Director actually runs.
            Environment.SetEnvironmentVariable(TerminalStateDetector.ContentRuleVariable, null);
            using (var off = new TerminalStateDetector(_manager, driveState: false))
                Assert.Equal(TurnContentRule.Off, off.ContentRule);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TerminalStateDetector.ContentRuleVariable, previous);
        }
    }

    // ------------------------------------------------------------------------------------------
    // The interface is the seam: the Director runs the rule it was handed, not a function beside it
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_detector_obeys_the_row_rule_it_was_handed()
    {
        // The defect this pins: the detector used to call the rule FUNCTIONS directly and repeat
        // the size comparison beside them, so the interface that is meant to be the single seam
        // was used by nobody in production. Work item five scores the interface implementations,
        // so as written it could have scored one function while the Director ran another - with
        // every test green. Here the injected rule opens the turn on a row the shipped row rule
        // holds red. If the detector is not asking the rule it holds, this session stays red.
        TurnDetectionShadowLog.Enabled = true;
        var opensEverything = new StubRule(opens: true);
        var (session, backend, detector) = Start(TurnContentRule.Row, rowRule: opensEverything);
        using (detector)
        {
            await SettleWithABody(session, backend);

            var working = NextTransitionTo(session, ActivityState.Working);
            backend.Write(Encoding.UTF8.GetBytes("Update installed - restart to apply\r\n> "));

            Assert.True(await working.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "the detector did not take its verdict from the rule it was handed");
            Assert.True(opensEverything.Calls > 0, "the injected row rule was never asked");
        }
    }

    [Fact]
    public async Task The_detector_holds_red_when_the_row_rule_it_was_handed_says_nothing_was_gained()
    {
        // The other direction, and the one that catches a detector that consults its rule and then
        // second-guesses it: real prose, which the shipped row rule opens on, must stay red when
        // the rule in force says nothing was gained.
        TurnDetectionShadowLog.Enabled = true;
        var rule = new StubRule(opens: true);
        var (session, backend, detector) = Start(TurnContentRule.Row, rowRule: rule);
        using (detector)
        {
            // The settling turn has to open, or the session never reaches the settled state this
            // test is about. The rule is turned down only once the session is red.
            await SettleWithABody(session, backend);
            rule.Opens = false;

            int baseline = CountShadowRows(session.Id);
            backend.Write(Encoding.UTF8.GetBytes("All nine call sites now go through one helper.\r\n> "));
            await WaitForShadowRowAfter(session.Id, baseline);
            await Task.Delay(SettleDelay);

            Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
        }
    }

    [Fact]
    public async Task The_size_verdict_and_the_numbers_beside_it_come_from_the_same_rule()
    {
        // The threshold comparison belongs INSIDE the size rule, and the magnitude written into
        // the log has to be the one that verdict was taken from. A detector that compares against
        // its own copy of the constant can write down a number it did not rule on.
        TurnDetectionShadowLog.Enabled = true;
        var size = new StubSizeRule(threshold: 7, magnitude: 42, opens: true);
        var (session, backend, detector) = Start(TurnContentRule.Size, sizeRule: size);
        using (detector)
        {
            await SettleWithABody(session, backend);

            int baseline = CountShadowRows(session.Id);
            var working = NextTransitionTo(session, ActivityState.Working);
            backend.Write(Encoding.UTF8.GetBytes("Update installed - restart to apply\r\n> "));

            Assert.True(await working.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "the detector did not take its size verdict from the rule it was handed");

            var row = await WaitForShadowRowAfter(session.Id, baseline);
            Assert.Equal(7, row.GetProperty("SizeThreshold").GetInt32());
            Assert.Equal(42, row.GetProperty("ChangedCharacters").GetInt32());
            Assert.True(row.GetProperty("SizeRuleOpens").GetBoolean());
        }
    }

    /// <summary>A candidate that always answers the same way, so the test can tell whether the
    /// detector asked it at all.</summary>
    private sealed class StubRule : ITerminalNoveltyRule
    {
        private int _calls;

        internal StubRule(bool opens) => Opens = opens;

        /// <summary>Settable, because a session can only reach SETTLED by working first, and with
        /// the rule on it is this rule that decides whether it ever works at all.</summary>
        internal bool Opens { get; set; }

        /// <summary>Throw ONCE, the next time the detector asks. A check faults for all sorts of
        /// ordinary reasons - a disposed buffer, a raced screen read - and what matters is that the
        /// burst it was asked about survives the fault.</summary>
        internal bool ThrowOnNextCall { get; set; }

        public string Name => "stub";
        internal int Calls => Volatile.Read(ref _calls);

        public bool GainedContent(
            IReadOnlyList<string> settledBody, IReadOnlyList<string> currentBody,
            IReadOnlyCollection<string> chromeMarkers, out string? evidence)
        {
            Interlocked.Increment(ref _calls);
            if (ThrowOnNextCall)
            {
                ThrowOnNextCall = false;
                throw new InvalidOperationException("the rule faulted while reading the screen");
            }
            bool opens = Opens;
            evidence = opens ? "the rule that was handed in said so" : null;
            return opens;
        }
    }

    /// <summary>A size candidate with numbers nothing else in the product could produce, so a log
    /// row carrying them proves where they came from.</summary>
    private sealed class StubSizeRule : ITerminalSizeRule
    {
        private readonly bool _opens;
        private readonly int _magnitude;

        internal StubSizeRule(int threshold, int magnitude, bool opens)
        {
            Threshold = threshold;
            _magnitude = magnitude;
            _opens = opens;
        }

        public string Name => "stub-size";
        public int Threshold { get; }

        public int Measure(IReadOnlyList<string>? settledBody, IReadOnlyList<string>? currentBody)
            => _magnitude;

        public bool GainedContent(
            IReadOnlyList<string> settledBody, IReadOnlyList<string> currentBody,
            IReadOnlyCollection<string> chromeMarkers, out string? evidence)
        {
            evidence = _opens ? $"changed {_magnitude} characters" : null;
            return _opens;
        }
    }

    // ------------------------------------------------------------------------------------------

    private (Session session, BufferOnlyBackend backend, TerminalStateDetector detector) Start(
        TurnContentRule rule,
        ITerminalNoveltyRule? rowRule = null,
        ITerminalSizeRule? sizeRule = null,
        TimeSpan? maxDeferral = null,
        AgentKind agent = AgentKind.ClaudeCode,
        Action<ActivityState, ActivityState>? beforeDetector = null)
    {
        var backend = new BufferOnlyBackend();
        var session = _manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        session.IsBrandNew = false;
        session.AgentKind = agent;

        // Subscribed BEFORE the detector wires itself, so this handler runs FIRST and a throw from
        // it reaches the detector mid-write - which is the only way to exercise what happens when
        // the state write faults.
        if (beforeDetector is not null) session.OnActivityStateChanged += beforeDetector;

        var detector = new TerminalStateDetector(
            _manager, driveState: true, Quiet, activityProducer: null,
            contentRule: rule, settleCheckDelay: SettleDelay,
            maxSettleCheckDeferral: maxDeferral ?? MaxDeferral,
            rowRule: rowRule, sizeRule: sizeRule);
        detector.Start();
        return (session, backend, detector);
    }

    /// <summary>Drive one turn all the way to settled, so a settled screen exists to compare against.</summary>
    private static async Task SettleWithABody(Session session, BufferOnlyBackend backend)
    {
        var settled = NextTransitionTo(session, ActivityState.WaitingForInput);
        backend.Write(Encoding.UTF8.GetBytes("The deployment finished successfully\r\n> "));
        Assert.True(await settled.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            "the session never settled, so there is no settled screen to compare against");
    }

    private static TaskCompletionSource<bool> NextTransitionTo(Session session, ActivityState target)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnActivityStateChanged += (_, newState) =>
        {
            if (newState == target) source.TrySetResult(true);
        };
        return source;
    }

    /// <summary>Every state this session moved to, in order.</summary>
    private async Task<string[]> RunSettleThenRepaint(TurnContentRule rule)
    {
        var (session, backend, detector) = Start(rule);
        using (detector)
        {
            var seen = new List<string>();
            var woke = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnActivityStateChanged += (_, newState) =>
            {
                lock (seen)
                {
                    seen.Add(newState.ToString());
                    if (seen.Count >= 3) woke.TrySetResult(true);
                }
            };

            backend.Write(Encoding.UTF8.GetBytes("The deployment finished successfully\r\n> "));
            await Task.Delay(Quiet + TimeSpan.FromMilliseconds(250));
            backend.Write(Encoding.UTF8.GetBytes("Update installed - restart to apply\r\n> "));

            await woke.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(SettleDelay + TimeSpan.FromMilliseconds(200));

            lock (seen) return seen.ToArray();
        }
    }

    private static string ShadowPath(Guid sessionId)
        => Path.Combine(CcStorage.TurnDetectionShadow(), sessionId.ToString("N") + ".jsonl");

    private static int CountShadowRows(Guid sessionId) => ShadowRows(sessionId).Length;

    /// <summary>
    /// The newest shadow row once a NEW one has appeared past <paramref name="baseline"/>.
    ///
    /// The baseline is not tidiness. The first turn a session runs also produces a check - it is a
    /// settled session receiving bytes, which is the whole trigger - so a helper that simply waited
    /// for "at least one row" returned that first row every time and quietly asserted against the
    /// wrong check. Both of the failures this helper was written twice for looked like production
    /// defects and were this.
    /// </summary>
    private static async Task<JsonElement> WaitForShadowRowAfter(Guid sessionId, int baseline)
    {
        var path = ShadowPath(sessionId);
        // Fifteen seconds, not five. These tests drive the detector'"'"'s REAL timers, and a check that
        // is merely late under load is not the same fact as a row that was never written - a five
        // second deadline turned the first into the second about once in fifteen runs when a build
        // was running beside the suite. What is asserted is unchanged: the row must appear.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var lines = ShadowRows(sessionId);
            if (lines.Length > baseline)
                return JsonDocument.Parse(lines[^1]).RootElement.Clone();
            await Task.Delay(25);
        }

        // An empty result must read as a broken instrument, never as a clean run - so the
        // failure carries what WAS in the directory, which is the difference between "the check
        // never ran" and "the row went somewhere else".
        var dir = Path.GetDirectoryName(path)!;
        var present = Directory.Exists(dir)
            ? string.Join(", ", Directory.GetFiles(dir).Select(f => $"{Path.GetFileName(f)}:{new FileInfo(f).Length}b"))
            : "(the directory does not exist)";
        throw new Xunit.Sdk.XunitException(
            $"no shadow row was written past row {baseline} in {path}; the directory holds {present}");
    }

    private static string[] ShadowRows(Guid sessionId)
    {
        var path = ShadowPath(sessionId);
        if (!File.Exists(path)) return Array.Empty<string>();
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
    }
}
