using System.Text;
using System.Text.Json;
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

    private (Session session, BufferOnlyBackend backend, TerminalStateDetector detector) Start(
        TurnContentRule rule)
    {
        var backend = new BufferOnlyBackend();
        var session = _manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        session.IsBrandNew = false;

        var detector = new TerminalStateDetector(
            _manager, driveState: true, Quiet, activityProducer: null,
            contentRule: rule, settleCheckDelay: SettleDelay, maxSettleCheckDeferral: MaxDeferral);
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
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var lines = ShadowRows(sessionId);
            if (lines.Length > baseline)
                return JsonDocument.Parse(lines[^1]).RootElement.Clone();
            await Task.Delay(25);
        }

        // An empty result must read as a broken instrument, never as a clean run.
        throw new Xunit.Sdk.XunitException(
            $"no shadow row was written past row {baseline} in {path}");
    }

    private static string[] ShadowRows(Guid sessionId)
    {
        var path = ShadowPath(sessionId);
        if (!File.Exists(path)) return Array.Empty<string>();
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
    }
}
