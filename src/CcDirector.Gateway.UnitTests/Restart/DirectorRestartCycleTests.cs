using CcDirector.ControlApi.Restart;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// The Director-side cycle - issue #2725. What it proves: the ORDER of the gates, that every gate that
/// says no stops the cycle with that gate's own words, that the launcher is asked ONLY after the drain
/// and the re-check both passed, and that two cycles cannot run at once.
/// </summary>
public sealed class DirectorRestartCycleTests
{
    private sealed class Drain : IRestartCycleDrain
    {
        public RestartDrainOutcome Outcome = new(RestartDrainVerdict.Drained, "ws-1", "every seat reached a clean stop");
        public int Calls;
        public RestartDrainAvailability Availability => new(true, "test drain");
        public Task<RestartDrainOutcome> RunAsync(DirectorRestartCycleOrder order, Action<string> progress, CancellationToken ct)
        {
            Calls++;
            progress("collecting");
            return Task.FromResult(Outcome);
        }
    }

    private sealed class Gateway : IRestartCycleGateway
    {
        public MachineRestartCapabilityDto Capability = new()
        {
            Verdict = RestartVerdict.CanRestart, Reason = "can be restarted",
            GuardedRestart = CapabilityState.Available, GuardedRestartReason = "declares the guard",
        };
        public LauncherRestartAnswer LauncherAnswer = new(200, "{\"ok\":true,\"restarted\":true,\"onlyIfEmpty\":true,\"sessions\":0}");
        public int CapabilityChecks;
        public int LauncherAsks;
        public List<DirectorRestartProgressReport> Reports = new();
        public bool ReportsFail;

        public Task<MachineRestartCapabilityDto> CheckCapabilityAsync(string machine, CancellationToken ct)
        { CapabilityChecks++; return Task.FromResult(Capability); }

        public Task<LauncherRestartAnswer> AskOwnLauncherRestartOnlyIfEmptyAsync(string machine, string? exePath, CancellationToken ct)
        { LauncherAsks++; return Task.FromResult(LauncherAnswer); }

        public Task ReportAsync(string machine, string requestId, DirectorRestartProgressReport report, CancellationToken ct)
        {
            if (ReportsFail) throw new InvalidOperationException("Gateway unreachable");
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public DirectorRestartProgressReport Last => Reports[^1];
    }

    private static DirectorRestartCycleOrder Order() => new() { RequestId = "req-1", Machine = "SOREN_NORTH", Reason = "update" };
    private static DirectorRestartEligibilityDto Eligible(bool? e, string reason = "because") => new() { Eligible = e, Reason = reason };

    private static DirectorRestartCycle Cycle(IRestartCycleDrain drain, Gateway gateway, bool? eligible = true)
        => new(Order(), drain, gateway, () => Eligible(eligible, "eligibility said so"), @"C:\app\cc-director.exe");

    [Fact]
    public async Task The_whole_cycle_in_order_and_the_launcher_asked_last()
    {
        var drain = new Drain();
        var gateway = new Gateway();

        var final = await Cycle(drain, gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Completed, final);
        Assert.Equal(1, drain.Calls);
        Assert.Equal(1, gateway.CapabilityChecks);
        Assert.Equal(1, gateway.LauncherAsks);
        Assert.Equal(DirectorRestartRequestState.Completed, gateway.Last.State);
        Assert.Contains("ws-1", gateway.Last.Progress);
        Assert.Equal("ws-1", gateway.Last.WorkspaceId);
        // Every step reported, in order: eligibility, draining, the drain's own progress, re-check, ask, done.
        Assert.True(gateway.Reports.Count >= 5);
        Assert.All(gateway.Reports.Take(gateway.Reports.Count - 1), r => Assert.Equal(DirectorRestartRequestState.Accepted, r.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Not_the_launchers_Director_or_could_not_tell_stops_before_the_drain(bool? eligible)
    {
        var drain = new Drain();
        var gateway = new Gateway();

        var final = await Cycle(drain, gateway, eligible).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Equal(0, drain.Calls);
        Assert.Equal(0, gateway.CapabilityChecks);
        Assert.Equal(0, gateway.LauncherAsks);
        Assert.Equal(DirectorRestartRequestState.Abandoned, gateway.Last.State);
        Assert.Equal("eligibility said so", gateway.Last.Progress);
    }

    [Fact]
    public async Task A_build_with_no_drain_stops_before_touching_anything_and_says_so()
    {
        var gateway = new Gateway();
        var cycle = new DirectorRestartCycle(Order(), new NoDrainOnThisBuild(), gateway, () => Eligible(true), null);

        var final = await cycle.RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Contains("carries no drain", gateway.Last.Progress);
        Assert.Contains("#2723", gateway.Last.Progress);
        Assert.Equal(0, gateway.CapabilityChecks);
        Assert.Equal(0, gateway.LauncherAsks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Drained_without_a_workspace_record_is_not_drained_and_the_launcher_is_not_asked(string? workspace)
    {
        var drain = new Drain { Outcome = new(RestartDrainVerdict.Drained, workspace, "every seat closed") };
        var gateway = new Gateway();

        var final = await Cycle(drain, gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Equal(0, gateway.CapabilityChecks);
        Assert.Equal(0, gateway.LauncherAsks);
        Assert.Contains("named no workspace record", gateway.Last.Progress);
    }

    [Fact]
    public async Task The_gate_is_claimed_synchronously_and_a_second_claim_is_refused_until_the_first_runs_out()
    {
        var first = Cycle(new Drain(), new Gateway());
        var second = Cycle(new Drain(), new Gateway());
        Assert.True(first.TryClaim());
        Assert.True(first.TryClaim(), "re-claiming by the holder is not a second cycle");
        Assert.False(second.TryClaim());
        Assert.Same(first, DirectorRestartCycle.Running);

        // Running the holder releases the gate at its end.
        await first.RunAsync();
        Assert.Null(DirectorRestartCycle.Running);
        Assert.True(second.TryClaim());
        await second.RunAsync();
    }

    [Fact]
    public async Task The_report_before_the_ask_names_the_workspace_because_the_ask_may_never_return()
    {
        var gateway = new Gateway();
        await Cycle(new Drain(), gateway).RunAsync();
        var beforeAsk = gateway.Reports[^2];
        Assert.Equal(DirectorRestartRequestState.Accepted, beforeAsk.State);
        Assert.Contains("stopped before it can say so", beforeAsk.Progress);
        Assert.Equal("ws-1", beforeAsk.WorkspaceId);
    }

    [Fact]
    public async Task A_blocked_drain_stops_never_forces_and_names_the_record_holding_the_closed_seats()
    {
        var drain = new Drain { Outcome = new(RestartDrainVerdict.Blocked, "ws-blocked", "seat e1d7291b never declared it finished") };
        var gateway = new Gateway();

        var final = await Cycle(drain, gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Equal(0, gateway.LauncherAsks);
        Assert.Equal(0, gateway.CapabilityChecks);
        Assert.Contains("e1d7291b", gateway.Last.Progress);
        Assert.Contains("ws-blocked", gateway.Last.Progress);
        Assert.Equal("ws-blocked", gateway.Last.WorkspaceId);
    }

    public static TheoryData<RestartVerdict, CapabilityState> NotPermitted => new()
    {
        { RestartVerdict.CannotRestart, CapabilityState.Available },
        { RestartVerdict.Unknown, CapabilityState.Available },
        { RestartVerdict.CanRestart, CapabilityState.Unavailable },
        { RestartVerdict.CanRestart, CapabilityState.Unknown },
        { (RestartVerdict)99, CapabilityState.Available },
        { RestartVerdict.CanRestart, (CapabilityState)99 },
    };

    [Theory]
    [MemberData(nameof(NotPermitted))]
    public async Task After_the_drain_the_machine_is_checked_again_and_anything_but_a_guarded_yes_stops_before_the_ask(
        RestartVerdict verdict, CapabilityState guard)
    {
        var drain = new Drain();
        var gateway = new Gateway();
        gateway.Capability = new MachineRestartCapabilityDto
        {
            Verdict = verdict, Reason = "REASON-FROM-PHASE-1",
            GuardedRestart = guard, GuardedRestartReason = "GUARD-FROM-PHASE-1",
        };

        var final = await Cycle(drain, gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Equal(1, drain.Calls);
        Assert.Equal(1, gateway.CapabilityChecks);
        Assert.Equal(0, gateway.LauncherAsks);
        Assert.Contains("checked again", gateway.Last.Progress);
        Assert.Contains("ws-1", gateway.Last.Progress);
        if (verdict != RestartVerdict.CanRestart) Assert.Contains("REASON-FROM-PHASE-1", gateway.Last.Progress);
        if (guard != CapabilityState.Available) Assert.Contains("GUARD-FROM-PHASE-1", gateway.Last.Progress);
    }

    [Fact]
    public async Task A_launcher_refusal_is_reported_in_the_launchers_words_with_the_record()
    {
        var drain = new Drain();
        var gateway = new Gateway { LauncherAnswer = new(409, "{\"error\":\"refusing to restart: it is holding 1 live session\"}") };

        var final = await Cycle(drain, gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Equal(1, gateway.LauncherAsks);
        Assert.Contains("HTTP 409", gateway.Last.Progress);
        Assert.Contains("1 live session", gateway.Last.Progress);
        Assert.Contains("ws-1", gateway.Last.Progress);
    }

    [Fact]
    public async Task A_launcher_success_without_the_guard_acknowledged_is_reported_as_the_502_the_Gateway_turns_it_into()
    {
        // Phase 2's relay turns an unacknowledged success into a 502. From here that is a refusal: the
        // restart may already have happened, and the report says so in the Gateway's words.
        var gateway = new Gateway { LauncherAnswer = new(502, "{\"error\":\"answered success without confirming it applied that condition\"}") };

        var final = await Cycle(new Drain(), gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Contains("without confirming", gateway.Last.Progress);
    }

    [Fact]
    public async Task A_Gateway_that_cannot_be_reported_to_stops_the_cycle_and_forces_nothing()
    {
        var drain = new Drain();
        var gateway = new Gateway { ReportsFail = true };

        var final = await Cycle(drain, gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Equal(0, drain.Calls);
        Assert.Equal(0, gateway.LauncherAsks);
    }

    [Fact]
    public async Task Two_cycles_cannot_run_at_once_and_the_second_touches_nothing()
    {
        var gate = new TaskCompletionSource();
        var slowDrain = new SlowDrain(gate.Task);
        var gateway = new Gateway();
        var first = Cycle(slowDrain, gateway).RunAsync();

        // Wait until the first is inside its drain.
        await slowDrain.Entered.Task;
        Assert.NotNull(DirectorRestartCycle.Running);

        var secondDrain = new Drain();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Cycle(secondDrain, gateway).RunAsync());
        Assert.Contains("already running", ex.Message);
        Assert.Equal(0, secondDrain.Calls);

        gate.SetResult();
        Assert.Equal(DirectorRestartRequestState.Completed, await first);
        Assert.Null(DirectorRestartCycle.Running);
    }

    private sealed class SlowDrain : IRestartCycleDrain
    {
        public RestartDrainAvailability Availability => new(true, "test drain");
        private readonly Task _release;
        public readonly TaskCompletionSource Entered = new();
        public SlowDrain(Task release) => _release = release;
        public async Task<RestartDrainOutcome> RunAsync(DirectorRestartCycleOrder order, Action<string> progress, CancellationToken ct)
        {
            Entered.TrySetResult();
            await _release;
            return new RestartDrainOutcome(RestartDrainVerdict.Drained, "ws-slow", "done");
        }
    }
}

/// <summary>Is this Director the one its launcher would restart? Four answers, not two.</summary>
public sealed class RestartEligibilityTests
{
    private const string Supervised = @"C:\Users\soren\AppData\Local\cc-director\app\cc-director.exe";

    [Fact]
    public void The_default_instance_running_the_supervised_executable_is_eligible()
    {
        var dto = RestartEligibility.Judge("default", true, Supervised, Supervised);
        Assert.True(dto.Eligible);
        Assert.Contains("is the executable its launcher supervises", dto.Reason);
    }

    [Fact]
    public void A_named_instance_is_not_eligible_even_from_the_supervised_executable()
    {
        var dto = RestartEligibility.Judge("slot7", false, Supervised, Supervised);
        Assert.False(dto.Eligible);
        Assert.Contains("named instance 'slot7'", dto.Reason);
    }

    [Fact]
    public void A_development_slot_executable_is_not_eligible()
    {
        var dto = RestartEligibility.Judge("default", true, @"D:\repo\scripts\local-build\cc-director5.exe", Supervised);
        Assert.False(dto.Eligible);
        Assert.Contains("cc-director5.exe", dto.Reason);
        Assert.Contains("launcher supervises", dto.Reason);
    }

    [Fact]
    public void An_unreadable_process_path_is_could_not_tell_not_no_and_not_yes()
    {
        var dto = RestartEligibility.Judge("default", true, null, Supervised);
        Assert.Null(dto.Eligible);
        Assert.Contains("could not read its own executable path", dto.Reason);
    }

    [Fact]
    public void A_process_inside_the_supervised_application_bundle_is_eligible()
    {
        var bundle = "/Users/soren/Applications/Director.app";
        var dto = RestartEligibility.Judge("default", true, bundle + "/Contents/MacOS/cc-director", bundle);
        Assert.True(dto.Eligible);
    }

    [Fact]
    public void Letter_case_does_not_make_the_same_Windows_executable_a_different_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dto = RestartEligibility.Judge("default", true, Supervised.ToUpperInvariant(), Supervised);
        Assert.True(dto.Eligible);
    }
}
