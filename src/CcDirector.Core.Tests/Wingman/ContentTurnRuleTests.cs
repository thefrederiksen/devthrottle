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
            var row = await WaitForShadowRowAfter(session.Id, baseline, detector);

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

            await WaitForShadowRowAfter(session.Id, baseline, detector);
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
            await WaitForShadowRowAfter(session.Id, baseline, detector);
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

            var row = await WaitForShadowRowAfter(session.Id, baseline, detector);

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
            await WaitForShadowRowAfter(session.Id, baseline, detector);
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
            await WaitForShadowRowAfter(session.Id, baseline, detector);
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
                await WaitForShadowRowAfter(session.Id, baseline, detector);
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
            await WaitForShadowRowAfter(session.Id, baseline, detector);
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

            var row = await WaitForShadowRowAfter(session.Id, baseline, detector);
            Assert.Equal(7, row.GetProperty("SizeThreshold").GetInt32());
            Assert.Equal(42, row.GetProperty("ChangedCharacters").GetInt32());
            Assert.True(row.GetProperty("SizeRuleOpens").GetBoolean());
        }
    }

    // ------------------------------------------------------------------------------------------
    // A check that keeps faulting must fall back to today's rule, never drop the turn
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_check_that_faults_past_the_retry_bound_opens_the_turn_rather_than_losing_it()
    {
        // The retry bound was invented to stop a spin and it built the exact failure this design
        // forbids. Past the bound the burst was DROPPED: a one-burst reply then had no pending
        // check and no later byte to make one, and the session sat red for ever. An inspection
        // demonstrated it with a rule that faulted on every call - four attempted checks, and the
        // session still WaitingForInput with nothing left to ask again.
        //
        // A rule that cannot decide must never decide against the user. Past the bound the turn
        // OPENS, which is today's behaviour and always available: the cost of a wrong open is a
        // blue session that settles again a quiet window later, and the cost of a wrong drop is
        // work that sits red until somebody happens to look at it.
        TurnDetectionShadowLog.Enabled = true;
        var rule = new StubRule(opens: true) { ThrowAlways = true };
        var (session, backend, detector) = Start(TurnContentRule.Row, rowRule: rule);
        using (detector)
        {
            await SettleWithABody(session, backend);

            var working = NextTransitionTo(session, ActivityState.Working);

            // ONE burst and nothing after it, exactly as in the inspection's probe.
            backend.Write(Encoding.UTF8.GetBytes("All nine call sites now go through one helper.\r\n> "));

            Assert.True(await Reached(working, TimeSpan.FromSeconds(10)),
                "a check that faulted past the retry bound lost the burst, so the turn never opened");

            // And it really did exhaust the bound rather than opening on the first fault - the
            // retries are what make this the LAST resort rather than the first.
            Assert.True(rule.Calls > 1,
                $"the turn opened after only {rule.Calls} attempt(s); the burst must be retried before the fallback");
        }
    }

    // ------------------------------------------------------------------------------------------
    // A faulted state write must not leave the session blue for ever - on EITHER shipped path
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_faulted_state_write_on_the_byte_path_does_not_leave_the_session_stuck_working()
    {
        // THE SWITCH-OFF PATH, which is what every Director runs today - this fault is on
        // origin/main and is older than the content rule. Session.SetActivityState assigns Working
        // and THEN calls its subscribers, so a subscriber that throws escapes from the middle of
        // the write. The byte callback swallowed it, the quiet timer was never armed, and the
        // active latch stayed set - so every later byte took the already-active branch and armed
        // nothing. Permanently blue, with nothing left that could bring it back.
        TurnDetectionShadowLog.Enabled = true;
        int throwsLeft = 0;
        var (session, backend, detector) = Start(
            TurnContentRule.Off,
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

            // ONE burst. Nothing arrives afterwards to rescue it.
            backend.Write(Encoding.UTF8.GetBytes("All nine call sites now go through one helper.\r\n> "));

            Assert.True(await Reached(red, TimeSpan.FromSeconds(10)),
                "the session never came back to red, so the faulted write left it latched Working");
        }
    }

    [Fact]
    public async Task A_faulted_state_write_on_the_body_path_does_not_leave_the_session_stuck_working()
    {
        // The same fault on the other shipped activation path: an agent whose idle terminal never
        // goes byte-silent (Grok) is woken by a BODY change rather than by a byte, through a
        // different method with its own copy of the latch-then-write order.
        TurnDetectionShadowLog.Enabled = true;
        int throwsLeft = 0;
        var (session, backend, detector) = Start(
            TurnContentRule.Off,
            agent: AgentKind.Grok,
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

            // Past the body-check throttle, and a real body change rather than a footer frame.
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            backend.Write(Encoding.UTF8.GetBytes("\r\nAll nine call sites now go through one helper.\r\n> "));

            Assert.True(await Reached(red, TimeSpan.FromSeconds(10)),
                "the session never came back to red, so the faulted write left it latched Working");
        }
    }

    // ------------------------------------------------------------------------------------------
    // The size rule is asked ONCE, so its verdict and its numbers cannot disagree
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_size_verdict_and_its_numbers_come_from_one_call()
    {
        // The detector used to gather the verdict, the magnitude and the threshold from three
        // separate calls, and an inspection pointed out that nothing REQUIRED the number written
        // into the log to be the number the verdict was taken from. The rule here answers with a
        // different magnitude every time it is asked, alternating above and below its own
        // threshold: a detector that asks twice writes a row that contradicts itself.
        TurnDetectionShadowLog.Enabled = true;
        var size = new DriftingSizeRule();
        var (session, backend, detector) = Start(TurnContentRule.Size, sizeRule: size);
        using (detector)
        {
            await SettleWithABody(session, backend);

            int before = size.Calls;
            int baseline = CountShadowRows(session.Id);
            backend.Write(Encoding.UTF8.GetBytes("Update installed - restart to apply\r\n> "));

            var row = await WaitForShadowRowAfter(session.Id, baseline, detector);

            bool opens = row.GetProperty("SizeRuleOpens").GetBoolean();
            int magnitude = row.GetProperty("ChangedCharacters").GetInt32();
            int threshold = row.GetProperty("SizeThreshold").GetInt32();

            Assert.Equal(magnitude >= threshold, opens);
            Assert.Equal(1, size.Calls - before);
        }
    }

    // ------------------------------------------------------------------------------------------
    // The detector the product builds runs the timings the product ships
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_detector_built_the_way_the_product_builds_one_runs_the_shipped_timings()
    {
        // The named constants were pinned; the CONSTRUCTOR'S USE of them was not. An inspection
        // substituted 401 milliseconds and four seconds in the constructor, left both constants
        // alone, and all 102 focused tests stayed green - because every behaviour test injects its
        // own timings and nothing else could see what a production detector took.
        using var detector = new TerminalStateDetector(_manager, driveState: false);

        Assert.Equal(TerminalStateDetector.SettleCheckDelay, detector.SettleCheckDelayInUse);
        Assert.Equal(TerminalStateDetector.MaxSettleCheckDeferral, detector.MaxSettleCheckDeferralInUse);
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

        /// <summary>Throw on EVERY call, which is what a permanent fault looks like - a screen read
        /// that cannot succeed rather than one that raced.</summary>
        internal bool ThrowAlways { get; set; }

        public string Name => "stub";
        internal int Calls => Volatile.Read(ref _calls);

        public bool GainedContent(
            IReadOnlyList<string> settledBody, IReadOnlyList<string> currentBody,
            IReadOnlyCollection<string> chromeMarkers, out string? evidence)
        {
            Interlocked.Increment(ref _calls);
            if (ThrowAlways)
                throw new InvalidOperationException("the rule faults on every call");
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
        private int _calls;

        internal StubSizeRule(int threshold, int magnitude, bool opens)
        {
            Threshold = threshold;
            _magnitude = magnitude;
            _opens = opens;
        }

        public string Name => "stub-size";
        public int Threshold { get; }
        internal int Calls => Volatile.Read(ref _calls);

        public SizeVerdict Evaluate(IReadOnlyList<string>? settledBody, IReadOnlyList<string>? currentBody)
        {
            Interlocked.Increment(ref _calls);
            return new SizeVerdict(_opens, _magnitude, Threshold,
                _opens ? $"changed {_magnitude} characters" : null);
        }

        public bool GainedContent(
            IReadOnlyList<string> settledBody, IReadOnlyList<string> currentBody,
            IReadOnlyCollection<string> chromeMarkers, out string? evidence)
        {
            var verdict = Evaluate(settledBody, currentBody);
            evidence = verdict.Evidence;
            return verdict.Opens;
        }
    }

    /// <summary>
    /// A size candidate whose magnitude CHANGES on every call, alternating above and below its own
    /// threshold. It exists to make a detector that asks more than once contradict itself: the
    /// verdict would come from one measurement and the number written into the log from another,
    /// and the row would say "this opened" beside a magnitude under the threshold. A rule asked
    /// exactly once cannot produce that row whatever it answers.
    /// </summary>
    private sealed class DriftingSizeRule : ITerminalSizeRule
    {
        private int _calls;

        public string Name => "drifting-size";
        public int Threshold => 100;
        internal int Calls => Volatile.Read(ref _calls);

        public SizeVerdict Evaluate(IReadOnlyList<string>? settledBody, IReadOnlyList<string>? currentBody)
        {
            // 400, then 4, then 400, then 4 ... the first answer opens and the second does not.
            int magnitude = Interlocked.Increment(ref _calls) % 2 == 1 ? 400 : 4;
            bool opens = magnitude >= Threshold;
            return new SizeVerdict(opens, magnitude, Threshold,
                opens ? $"changed {magnitude} characters" : null);
        }

        public bool GainedContent(
            IReadOnlyList<string> settledBody, IReadOnlyList<string> currentBody,
            IReadOnlyCollection<string> chromeMarkers, out string? evidence)
        {
            var verdict = Evaluate(settledBody, currentBody);
            evidence = verdict.Evidence;
            return verdict.Opens;
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

    /// <summary>
    /// Wait for a transition and answer whether it arrived, rather than throwing when it did not.
    ///
    /// Task.WaitAsync throws TimeoutException, and it throws BEFORE Assert.True can look at the
    /// result - so every one of these tests reported "The operation has timed out" and threw away
    /// the sentence describing what had actually gone wrong. The symptom a test reports is the
    /// whole value of watching it fail; a generic timeout tells the next reader nothing about which
    /// guarantee broke.
    /// </summary>
    private static async Task<bool> Reached(TaskCompletionSource<bool> transition, TimeSpan within)
        => await Task.WhenAny(transition.Task, Task.Delay(within)) == transition.Task;

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
    private static async Task<JsonElement> WaitForShadowRowAfter(
        Guid sessionId, int baseline, TerminalStateDetector? detector = null)
    {
        var path = ShadowPath(sessionId);
        // Fifteen seconds, not five. These tests drive the detector's REAL timers, and a check that
        // is merely late under load is not the same fact as a row that was never written - a five
        // second deadline turned the first into the second about once in fifteen runs when a build
        // was running beside the suite. What is asserted is unchanged: the row must appear.
        //
        // The longer wait changes no production behaviour and diagnoses nothing on its own - it
        // only makes the flake less likely. That is a tolerance change, and it was once written up
        // as though it had settled the question. The POSITIVE SIGNAL below is what actually
        // separates the two facts.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var lines = ShadowRows(sessionId);
            if (lines.Length > baseline)
                return JsonDocument.Parse(lines[^1]).RootElement.Clone();
            await Task.Delay(25);
        }

        // An empty result must read as a broken instrument, never as a clean run - so the failure
        // carries what WAS in the directory, which is the difference between "the check never ran"
        // and "the row went somewhere else".
        var dir = Path.GetDirectoryName(path)!;
        var present = Directory.Exists(dir)
            ? string.Join(", ", Directory.GetFiles(dir).Select(f => $"{Path.GetFileName(f)}:{new FileInfo(f).Length}b"))
            : "(the directory does not exist)";

        // THE ONE FACT THE DIRECTORY CANNOT CARRY. A target file holding exactly the baseline rows
        // is the same observation whether the row is LATE or will NEVER be written, so the file
        // listing alone can never tell them apart - an inspection made that point and it was right.
        // A check that is still armed at the deadline is a positive signal unique to "late": the
        // callback is scheduled and has not fired. No check armed, and no row, means nothing is
        // going to write one.
        //
        // Callers that do not pass the detector get the honest version of that sentence rather than
        // a guess.
        var lateness = detector is null
            ? "no detector was handed to this helper, so late and never CANNOT be told apart here"
            : detector.HasPendingCheck(sessionId)
                ? "a check IS still armed for this session, so the row is LATE rather than absent"
                : "NO check is armed for this session, so no row will ever be written for this burst";

        throw new Xunit.Sdk.XunitException(
            $"no shadow row was written past row {baseline} in {path}; the directory holds {present}; {lateness}");
    }

    private static string[] ShadowRows(Guid sessionId)
    {
        var path = ShadowPath(sessionId);
        if (!File.Exists(path)) return Array.Empty<string>();
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
    }
}
