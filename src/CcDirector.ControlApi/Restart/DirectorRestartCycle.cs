using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Restart;

/// <summary>What the drain step reports back to the cycle.</summary>
public enum RestartDrainVerdict
{
    /// <summary>Every seat reached a clean stop; the record is on the Gateway under the workspace named.</summary>
    Drained,

    /// <summary>A seat could not be closed cleanly. Nothing was forced; the seats already closed are in the
    /// record named, and the restart must not proceed.</summary>
    Blocked,

    /// <summary>This build cannot drain at all. Nothing was touched.</summary>
    Unavailable,
}

/// <summary>The drain step's outcome: the verdict, the record it wrote (when it wrote one), and why.</summary>
public sealed record RestartDrainOutcome(RestartDrainVerdict Verdict, string? WorkspaceId, string Detail);

/// <summary>Whether a build can drain, and the sentence that says why.</summary>
public sealed record RestartDrainAvailability(bool Available, string Reason);

/// <summary>
/// The drain, behind a seam. Issue #2723 builds the drain itself; this is the shape the cycle calls it
/// through, so the cycle's own rules - order, gates, never forcing - can be proved without a fleet.
/// </summary>
public interface IRestartCycleDrain
{
    /// <summary>Whether this build can drain at all, and why. Asked BEFORE the owner is shown a request, so
    /// a cycle that would stop at its first step is never offered for approval.</summary>
    RestartDrainAvailability Availability { get; }

    /// <summary>Drain this Director to a record on the Gateway.</summary>
    /// <param name="order">What was accepted, for the record.</param>
    /// <param name="progress">Called with one sentence on every state change.</param>
    /// <param name="ct">Cancellation.</param>
    Task<RestartDrainOutcome> RunAsync(DirectorRestartCycleOrder order, Action<string> progress, CancellationToken ct);
}

/// <summary>
/// THE DRAIN THIS BUILD DOES NOT HAVE. The Director-side drain is issue #2723 and it has not merged into
/// this tree, so the honest answer is a refusal that says so - never a cycle that skips the drain and
/// asks the launcher anyway, which the launcher would refuse while sessions are live but which on an
/// empty Director would restart it without a record. Replaced by the real step on the tree that carries
/// the drain; the seam is one class.
/// </summary>
public sealed class NoDrainOnThisBuild : IRestartCycleDrain
{
    private const string Why =
        "this Director build carries no drain - the drain inside the Director (issue #2723) is not part of "
        + "it - so a restart cycle would stop at its first step, before closing anything. Update this Director "
        + "to a build that carries the drain and ask again.";

    /// <inheritdoc />
    public RestartDrainAvailability Availability => new(false, Why);

    /// <inheritdoc />
    public Task<RestartDrainOutcome> RunAsync(DirectorRestartCycleOrder order, Action<string> progress, CancellationToken ct)
        => Task.FromResult(new RestartDrainOutcome(RestartDrainVerdict.Unavailable, null, Why));
}

/// <summary>The launcher's answer to a guarded restart, as the Gateway relayed it.</summary>
/// <param name="Status">The HTTP status the Gateway's restart route answered with. 2xx means the launcher
/// accepted the guarded restart and this Director is about to be stopped.</param>
/// <param name="Body">The body, verbatim, for the report.</param>
public sealed record LauncherRestartAnswer(int Status, string Body);

/// <summary>Everything the cycle needs from the Gateway, behind a seam.</summary>
public interface IRestartCycleGateway
{
    /// <summary>The Phase 1 capability answer for a machine. Throws when the Gateway cannot answer.</summary>
    Task<MachineRestartCapabilityDto> CheckCapabilityAsync(string machine, CancellationToken ct);

    /// <summary>Ask this Director's OWN launcher, through the Gateway's restart route on this Director's
    /// credential, to restart it ONLY IF it is empty. Does not throw on a refusal; the answer carries it.</summary>
    Task<LauncherRestartAnswer> AskOwnLauncherRestartOnlyIfEmptyAsync(string machine, string? exePath, CancellationToken ct);

    /// <summary>Report progress or the outcome against the request. Throws when the Gateway cannot be reached.</summary>
    Task ReportAsync(string machine, string requestId, DirectorRestartProgressReport report, CancellationToken ct);
}

/// <summary>
/// THE CYCLE - issue #2725 (restart epic, Phase 6). After the owner accepts, nothing is asked of anybody:
/// this Director checks it is the one its launcher would restart, drains itself to a record, checks the
/// machine AGAIN, asks its own launcher for a guarded restart, and reports once at every step.
///
/// THE ORDER IS THE SAFETY. Eligibility before the drain, because draining the wrong Director is the
/// most expensive mistake available. The drain before the capability re-check, because the check is
/// about the moment the launcher is asked and that moment is after the drain. The re-check immediately
/// before the ask, and NOT optimised away as a duplicate of the Gateway's: the machine may have changed
/// since the owner tapped accept, and only-if-empty is a guarantee only against a launcher that has
/// DECLARED it - against an older one it is detection after the fact, because the Director has already
/// been restarted by the time the unacknowledged answer returns. Re-running the check here is what
/// closes that loop.
///
/// IT NEVER FORCES. If any step cannot proceed the cycle stops, reports why in the step's own words, and
/// names the workspace record so the sessions already closed can be found. There is no force path and
/// what forcing should mean is undecided; no phase decides it.
///
/// ONE AT A TIME. Two cycles on one Director is two drains racing, so the second is refused before it
/// touches anything.
/// </summary>
public sealed class DirectorRestartCycle
{
    private static readonly object Gate = new();
    private static DirectorRestartCycle? _running;

    private readonly IRestartCycleDrain _drain;
    private readonly IRestartCycleGateway _gateway;
    private readonly Func<DirectorRestartEligibilityDto> _eligibility;
    private readonly string? _exePath;

    /// <summary>The cycle running on this Director right now, or null.</summary>
    public static DirectorRestartCycle? Running { get { lock (Gate) return _running; } }

    /// <summary>
    /// Claim the one-at-a-time gate for this cycle NOW, synchronously, before it is scheduled. False when
    /// another cycle holds it. A host that checked <see cref="Running"/> and then scheduled a cycle had a
    /// window in which two commands could both be told "taken" and only one could run - the other threw
    /// inside its task and nobody reported its request. Claiming here closes that window: the answer
    /// "taken" is given only to the cycle that holds the gate.
    /// </summary>
    public bool TryClaim()
    {
        lock (Gate)
        {
            if (_running is not null && !ReferenceEquals(_running, this)) return false;
            _running = this;
            return true;
        }
    }

    /// <summary>The order this cycle is running.</summary>
    public DirectorRestartCycleOrder Order { get; }

    /// <summary>Create a cycle for one order.</summary>
    /// <param name="order">What the owner accepted.</param>
    /// <param name="drain">The drain step.</param>
    /// <param name="gateway">The Gateway seam.</param>
    /// <param name="eligibility">This Director's own answer to "am I the launcher's Director?", read at run
    /// time rather than captured, so it reflects the process as it is.</param>
    /// <param name="exePath">This process's executable, sent to the restart route's slot guard as evidence.</param>
    public DirectorRestartCycle(DirectorRestartCycleOrder order, IRestartCycleDrain drain, IRestartCycleGateway gateway,
        Func<DirectorRestartEligibilityDto> eligibility, string? exePath)
    {
        Order = order ?? throw new ArgumentNullException(nameof(order));
        _drain = drain ?? throw new ArgumentNullException(nameof(drain));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        _exePath = exePath;
    }

    /// <summary>
    /// Run the cycle to its end. Returns the final state reported: Completed when the launcher accepted
    /// the guarded restart (this process is about to be stopped), Abandoned otherwise.
    /// </summary>
    public async Task<DirectorRestartRequestState> RunAsync(CancellationToken ct = default)
    {
        if (!TryClaim())
            throw new InvalidOperationException(
                $"a restart cycle is already running on this Director for request {Running?.Order.RequestId}; "
                + "two cycles on one Director is two drains racing, so this one is refused before it touches anything.");

        FileLog.Write($"[DirectorRestartCycle] RunAsync: request={Order.RequestId} machine={Order.Machine} askedBy={Order.RequestedBySessionName}");
        try
        {
            return await RunStepsAsync(ct);
        }
        catch (Exception ex)
        {
            // The one catch, at the cycle's entry point: whatever step threw, the outcome is that nothing
            // further happens and the reason reaches the request if the Gateway can still be reached.
            FileLog.Write($"[DirectorRestartCycle] FAILED request={Order.RequestId}: {ex}");
            await TryReportAsync(DirectorRestartRequestState.Abandoned,
                $"the restart cycle stopped with an error and nothing was forced: {ex.Message}", null, ct);
            return DirectorRestartRequestState.Abandoned;
        }
        finally
        {
            lock (Gate) { if (ReferenceEquals(_running, this)) _running = null; }
        }
    }

    private async Task<DirectorRestartRequestState> RunStepsAsync(CancellationToken ct)
    {
        // ---- 1. Am I the Director the launcher would restart? ----
        await ReportAsync(DirectorRestartRequestState.Accepted,
            "checking that this Director is the one its launcher supervises", null, ct);
        var eligibility = _eligibility();
        // == against the one answer that permits. Null (could not tell) and false both stop here.
        if (eligibility.Eligible != true)
            return await AbandonAsync(eligibility.Reason, null, ct);

        // ---- 2. Drain to a record on the Gateway. ----
        await ReportAsync(DirectorRestartRequestState.Accepted, "draining: every session is asked to write a handover and close, leaf-first", null, ct);
        var drained = await _drain.RunAsync(Order,
            progress => { FileLog.Write($"[DirectorRestartCycle] drain: {progress}"); _ = TryReportAsync(DirectorRestartRequestState.Accepted, progress, null, ct); },
            ct);
        switch (drained.Verdict)
        {
            case RestartDrainVerdict.Drained when string.IsNullOrWhiteSpace(drained.WorkspaceId):
                // DRAINED WITHOUT A RECORD IS NOT DRAINED. The workspace is what the restore reads and what a
                // reader of an abandoned request follows to the closed seats; a restart sent without one
                // would lose the fleet with every session reported closed. A drain that could not name its
                // record is treated as blocked, and the restart is not asked for.
                return await AbandonAsync(
                    "the drain reported every seat closed but named no workspace record, so there is nothing a "
                    + "restore could read. The restart is not asked for. " + drained.Detail, null, ct);
            case RestartDrainVerdict.Drained:
                break;
            case RestartDrainVerdict.Blocked:
                return await AbandonAsync(
                    "the drain stopped and the restart must not proceed: " + drained.Detail
                    + (drained.WorkspaceId is null ? "" : $" The sessions already closed are in workspace '{drained.WorkspaceId}'."),
                    drained.WorkspaceId, ct);
            case RestartDrainVerdict.Unavailable:
                return await AbandonAsync(drained.Detail, drained.WorkspaceId, ct);
            default:
                // A verdict this build does not know is not a drained Director.
                return await AbandonAsync($"the drain answered with a verdict this cycle does not know how to read ({drained.Verdict}), so the restart must not proceed.", drained.WorkspaceId, ct);
        }

        // ---- 3. The machine, checked AGAIN, immediately before the ask. Not a duplicate. ----
        await ReportAsync(DirectorRestartRequestState.Accepted, "drained; re-checking that the launcher can still be asked for a guarded restart", drained.WorkspaceId, ct);
        var capability = await _gateway.CheckCapabilityAsync(Order.Machine, ct);
        if (DirectorRestartGate.Refusal(capability) is { } refusal)
            return await AbandonAsync(
                "the machine was checked again after the drain and a guarded restart must not be sent: " + refusal
                + $" Every session is closed and recorded in workspace '{drained.WorkspaceId}'; restore from it or restart by hand.",
                drained.WorkspaceId, ct);

        // ---- 4. Ask my own launcher, only if empty. ----
        // THIS REPORT IS WRITTEN BEFORE THE ASK BECAUSE A SUCCESSFUL ASK MAY NEVER RETURN HERE. The
        // launcher answers only after it has stopped this process and started the next one, so the line
        // below is the last thing this process can say when the restart goes ahead. The next process,
        // seeded from the workspace named here, owes the final report (issue #2724); a record that never
        // moves past this line expires as unreported on the Gateway rather than reading "running" for ever.
        await ReportAsync(DirectorRestartRequestState.Accepted,
            $"asking this Director's launcher for a restart only if it is empty. If the restart goes ahead this process "
            + $"is stopped before it can say so; the Director that comes back reports the outcome. Its fleet is recorded in workspace '{drained.WorkspaceId}'.",
            drained.WorkspaceId, ct);
        var answer = await _gateway.AskOwnLauncherRestartOnlyIfEmptyAsync(Order.Machine, _exePath, ct);
        if (answer.Status is < 200 or >= 300)
            return await AbandonAsync(
                $"the launcher did not accept the guarded restart (HTTP {answer.Status}): {answer.Body} "
                + $"Every session is closed and recorded in workspace '{drained.WorkspaceId}'.",
                drained.WorkspaceId, ct);

        // From here this process is being stopped by its launcher. The restore after the gap is the new
        // process's work (issue #2724), seeded from the workspace named here.
        await ReportAsync(DirectorRestartRequestState.Completed,
            $"the launcher accepted the guarded restart; this Director is being stopped and started again. Its fleet is recorded in workspace '{drained.WorkspaceId}'.",
            drained.WorkspaceId, ct);
        FileLog.Write($"[DirectorRestartCycle] COMPLETED request={Order.RequestId} workspace={drained.WorkspaceId}");
        return DirectorRestartRequestState.Completed;
    }

    private async Task<DirectorRestartRequestState> AbandonAsync(string reason, string? workspaceId, CancellationToken ct)
    {
        FileLog.Write($"[DirectorRestartCycle] ABANDONED request={Order.RequestId}: {reason}");
        await ReportAsync(DirectorRestartRequestState.Abandoned, reason, workspaceId, ct);
        return DirectorRestartRequestState.Abandoned;
    }

    private Task ReportAsync(DirectorRestartRequestState state, string progress, string? workspaceId, CancellationToken ct)
        => _gateway.ReportAsync(Order.Machine, Order.RequestId,
            new DirectorRestartProgressReport { State = state, Progress = progress, WorkspaceId = workspaceId }, ct);

    private async Task TryReportAsync(DirectorRestartRequestState state, string progress, string? workspaceId, CancellationToken ct)
    {
        try { await ReportAsync(state, progress, workspaceId, ct); }
        catch (Exception ex) { FileLog.Write($"[DirectorRestartCycle] report FAILED request={Order.RequestId}: {ex.Message}"); }
    }
}
