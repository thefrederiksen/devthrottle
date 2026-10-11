using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the session state-transition emitter (issue #1771, spine item 2). The claims that matter: the
/// four activity states map to ledger states and Starting/Exited do not; an event lands only on a real change
/// (a heartbeat repeat is skipped); a session that exits mid-wait gets one closing event so its wait is not
/// overcounted; a session that exits while active needs no closer; a restarted session re-emits; and - the
/// Hosted Multi-Tenancy claim - two tenants sharing a raw session id keep independent dedup memory and
/// independent ledger rows (a bare session key let one tenant suppress or steal another's transitions).
/// </summary>
public sealed class SessionStateEventEmitterTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // Self-host shape: one Local tenant, an inert boundary (EnterScope is a no-op off the async-local path).
    private (SessionStateEventEmitter Emitter, GovernanceEventLedger Ledger) New()
    {
        var ledger = new GovernanceEventLedger(_h.Open());
        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false);
        return (new SessionStateEventEmitter(ledger, boundary), ledger);
    }

    private List<string> StatesFor(GovernanceEventLedger ledger, string sessionId) =>
        ledger.List(sessionId: sessionId).Select(e => e.State).ToList();

    [Theory]
    [InlineData("Working", "active")]
    [InlineData("Idle", "idle")]
    [InlineData("WaitingForInput", "waiting-on-human")]
    [InlineData("WaitingForPerm", "waiting-on-permission")]
    [InlineData("waitingforinput", "waiting-on-human")] // case-insensitive
    public void MapState_maps_the_four_activity_states(string activity, string expected)
    {
        Assert.Equal(expected, SessionStateEventEmitter.MapState(activity));
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Exited")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void MapState_returns_null_for_non_ledger_states(string? activity)
    {
        Assert.Null(SessionStateEventEmitter.MapState(activity));
    }

    [Fact]
    public void Observe_emits_only_on_a_real_transition()
    {
        var (emitter, ledger) = New();
        emitter.Observe(TenantId.Local, "s1", "Working");
        emitter.Observe(TenantId.Local, "s1", "Working");          // heartbeat repeat - no new event
        emitter.Observe(TenantId.Local, "s1", "WaitingForInput");
        emitter.Observe(TenantId.Local, "s1", "WaitingForInput");  // repeat - no new event

        var states = StatesFor(ledger, "s1");
        Assert.Equal(2, states.Count);
        Assert.Contains(GovernanceEventState.Active, states);
        Assert.Contains(GovernanceEventState.WaitingOnHuman, states);
    }

    [Fact]
    public void Observe_skips_starting_and_emits_nothing()
    {
        var (emitter, ledger) = New();
        emitter.Observe(TenantId.Local, "s1", "Starting");
        Assert.Empty(ledger.List(sessionId: "s1"));
    }

    [Fact]
    public void Exit_while_waiting_emits_one_closing_event()
    {
        var (emitter, ledger) = New();
        emitter.Observe(TenantId.Local, "s1", "WaitingForInput"); // open wait
        emitter.Observe(TenantId.Local, "s1", "Exited");          // exits mid-wait -> close the interval honestly

        var states = StatesFor(ledger, "s1");
        Assert.Equal(2, states.Count);
        Assert.Contains(GovernanceEventState.WaitingOnHuman, states);
        Assert.Contains(GovernanceEventState.Recovered, states); // the close-on-exit
    }

    [Fact]
    public void Exit_while_active_emits_no_closing_event()
    {
        var (emitter, ledger) = New();
        emitter.Observe(TenantId.Local, "s1", "Working"); // active, no open wait
        emitter.Observe(TenantId.Local, "s1", "Exited");  // no closer needed

        var states = StatesFor(ledger, "s1");
        Assert.Single(states);
        Assert.Equal(GovernanceEventState.Active, states[0]);
    }

    [Fact]
    public void A_restarted_session_after_exit_re_emits_its_first_state()
    {
        var (emitter, ledger) = New();
        emitter.Observe(TenantId.Local, "s1", "Working");
        emitter.Observe(TenantId.Local, "s1", "Exited");  // forgets s1 (no open wait, no closer)
        emitter.Observe(TenantId.Local, "s1", "Working"); // a fresh transition after the guard was cleared

        var active = ledger.List(sessionId: "s1", state: GovernanceEventState.Active);
        Assert.Equal(2, active.Count);
    }

    [Fact]
    public void Two_tenants_sharing_a_session_id_keep_independent_transitions()
    {
        // Hosted shape: an async-local context, so the emitter's per-observation EnterScope actually scopes
        // each ledger write to its owning tenant. One database file, two tenants, one shared raw session id.
        var ctx = new AsyncLocalTenantContext();
        var writeLedger = new GovernanceEventLedger(_h.Open(ctx));
        var boundary = new HostedTenantBoundary(ctx, new DeviceRegistry(), hosted: false);
        var emitter = new SessionStateEventEmitter(writeLedger, boundary);

        var alpha = new TenantId("alpha");
        var beta = new TenantId("beta");

        // Both accounts run a session that happens to share the raw id "s1".
        emitter.Observe(alpha, "s1", "Working");
        emitter.Observe(beta, "s1", "Working");           // must NOT be suppressed by alpha's dedup slot
        emitter.Observe(alpha, "s1", "WaitingForInput");
        emitter.Observe(beta, "s1", "WaitingForInput");

        // Read each tenant's ledger in isolation via the global query filter (fresh contexts, same file).
        var alphaStates = StatesFor(new GovernanceEventLedger(_h.Open(new FixedTenantContext(alpha))), "s1");
        var betaStates = StatesFor(new GovernanceEventLedger(_h.Open(new FixedTenantContext(beta))), "s1");

        // Neither tenant suppressed the other, and no row crossed over: each sees exactly its own two states.
        Assert.Equal(2, alphaStates.Count);
        Assert.Contains(GovernanceEventState.Active, alphaStates);
        Assert.Contains(GovernanceEventState.WaitingOnHuman, alphaStates);

        Assert.Equal(2, betaStates.Count);
        Assert.Contains(GovernanceEventState.Active, betaStates);
        Assert.Contains(GovernanceEventState.WaitingOnHuman, betaStates);
    }

    [Fact]
    public void One_tenants_exit_does_not_clear_anothers_dedup_memory()
    {
        var ctx = new AsyncLocalTenantContext();
        var writeLedger = new GovernanceEventLedger(_h.Open(ctx));
        var boundary = new HostedTenantBoundary(ctx, new DeviceRegistry(), hosted: false);
        var emitter = new SessionStateEventEmitter(writeLedger, boundary);

        var alpha = new TenantId("alpha");
        var beta = new TenantId("beta");

        emitter.Observe(alpha, "s1", "Working");
        emitter.Observe(beta, "s1", "Working");
        emitter.Observe(alpha, "s1", "Exited");   // forgets ONLY alpha's s1
        emitter.Observe(beta, "s1", "Working");   // a repeat for beta - still deduped, must NOT re-emit

        var betaActive = new GovernanceEventLedger(_h.Open(new FixedTenantContext(beta)))
            .List(sessionId: "s1", state: GovernanceEventState.Active);
        Assert.Single(betaActive); // beta emitted Active once; alpha's exit did not reset beta's dedup guard
    }
}
