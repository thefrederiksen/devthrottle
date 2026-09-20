using CcDirector.ControlApi.Drain;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Restart;

/// <summary>
/// THE DRAIN STEP OF THE RESTART CYCLE, OVER THE REAL DRAIN (issue #3169).
///
/// The cycle used to be wired to a stand-in that refused, so a restart asked for through the cycle never
/// drained anything. This is the step that runs <see cref="DirectorDrain"/> - the same engine the desktop
/// runs - and tells the cycle, in the cycle's three verdicts, what the drain found.
///
/// IT ADDS NO RULE OF ITS OWN. Whether the restart may proceed is the drain's answer
/// (<see cref="DirectorDrainResult.ReadyToRestart"/>) and only that. This class chooses nothing: it names
/// the record, hands the order's reason to the drain, passes progress on, and translates the result.
///
/// THE DRAIN COMES THROUGH A FACTORY because a drain is built per run (an instance carries one run's
/// closed seats and refuses to run twice) and because whether one can be built at all is the availability
/// answer: the factory returns null when this Director has no Gateway client, exactly as
/// <c>ControlApiHost.CreateDrain</c> does. One question, asked one way, so "available" and "ran" cannot
/// disagree.
/// </summary>
public sealed class DirectorDrainRestartStep : IRestartCycleDrain
{
    private const string NoGateway =
        "this Director is not connected to a Gateway, and a drain keeps its record on the Gateway so that it "
        + "can still be read while this machine is down. Without that record every session would be closed and "
        + "the only account of them would be on a disk nobody can reach, so a restart cycle would stop at its "
        + "first step, before closing anything. Connect this Director to a Gateway and ask again.";

    private const string GatewayPresent =
        "this Director can drain itself: it is connected to a Gateway, which is where the record of the drain is kept.";

    private readonly Func<Action<DrainProgress>, DirectorDrain?> _createDrain;
    private readonly string _directorName;
    private readonly string? _directory;
    private readonly Func<DateTime> _localNow;

    /// <summary>Create the step.</summary>
    /// <param name="createDrain">Builds a drain of this Director that reports progress to the handler given,
    /// or returns null when this Director has no Gateway client. Must be cheap and must change nothing: it
    /// is called to answer <see cref="Availability"/> before the owner is shown a request.</param>
    /// <param name="directorName">This Director's display name, which names the record.</param>
    /// <param name="directory">Where the handover documents go. Null uses the drain's standard location
    /// under the data root; a test passes its own.</param>
    /// <param name="localNow">Test seam for the local clock that stamps the record's name.</param>
    public DirectorDrainRestartStep(
        Func<Action<DrainProgress>, DirectorDrain?> createDrain,
        string directorName,
        string? directory = null,
        Func<DateTime>? localNow = null)
    {
        _createDrain = createDrain ?? throw new ArgumentNullException(nameof(createDrain));
        _directorName = string.IsNullOrWhiteSpace(directorName) ? "this Director" : directorName;
        _directory = directory;
        _localNow = localNow ?? (() => DateTime.Now);
    }

    /// <inheritdoc />
    public RestartDrainAvailability Availability
    {
        get
        {
            // Building a drain touches nothing: no session is read, nothing is sent, and the one-at-a-time
            // gate is taken by RunAsync, not by the constructor. The drain built here is thrown away.
            var available = _createDrain(_ => { }) is not null;
            FileLog.Write($"[DirectorDrainRestartStep] Availability: available={available}");
            return new RestartDrainAvailability(available, available ? GatewayPresent : NoGateway);
        }
    }

    /// <inheritdoc />
    public async Task<RestartDrainOutcome> RunAsync(DirectorRestartCycleOrder order, Action<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(progress);
        FileLog.Write($"[DirectorDrainRestartStep] RunAsync: request={order.RequestId}, reason={order.Reason}");

        // EACH CHANGE, ONCE. The drain reports on every poll - every ten seconds for up to ninety minutes -
        // and most polls change nothing. Every sentence passed on becomes a report to the Gateway, so a
        // sentence identical to the last one is not a change and is not sent.
        string? last = null;
        var drain = _createDrain(p =>
        {
            var sentence = Sentence(p);
            if (string.Equals(sentence, last, StringComparison.Ordinal)) return;
            last = sentence;
            progress(sentence);
        });
        if (drain is null)
        {
            FileLog.Write($"[DirectorDrainRestartStep] RunAsync: request={order.RequestId}, verdict=Unavailable (no Gateway client)");
            return new RestartDrainOutcome(RestartDrainVerdict.Unavailable, null, NoGateway);
        }

        var startedLocal = _localNow();
        var options = new DrainOptions
        {
            WorkspaceId = DrainPaths.WorkspaceIdFor(_directorName, startedLocal),
            WorkspaceName = $"{_directorName} restart {startedLocal:yyyy-MM-dd HH:mm}",
            Reason = string.IsNullOrWhiteSpace(order.Reason) ? null : order.Reason.Trim(),
            DrivenBySessionId = string.IsNullOrWhiteSpace(order.RequestedBySessionId) ? null : order.RequestedBySessionId,
            DrivenByNote = $"Run by the restart cycle for request {order.RequestId}"
                + (string.IsNullOrWhiteSpace(order.RequestedBySessionName) ? "." : $", asked for by {order.RequestedBySessionName}."),
        };

        DirectorDrainResult result;
        try
        {
            result = await drain.RunAsync(options, _directory, ct).ConfigureAwait(false);
        }
        catch (DrainAlreadyRunningException ex)
        {
            // NOT AN ERROR, AN ANSWER. Another drain holds this Director - the desktop's, most likely - and
            // this one touched nothing, so there is no record of its own to name. The cycle stops on the
            // drain's own sentence, which names the drain that does hold it and the record it is writing.
            FileLog.Write($"[DirectorDrainRestartStep] RunAsync: request={order.RequestId}, verdict=Blocked (a drain is already running): {ex.Message}");
            return new RestartDrainOutcome(RestartDrainVerdict.Blocked, null, ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorDrainRestartStep] RunAsync FAILED: request={order.RequestId}, workspace={options.WorkspaceId}: {ex.Message}");
            throw;
        }

        var workspaceId = string.IsNullOrWhiteSpace(result.Document.Id) ? options.WorkspaceId : result.Document.Id;
        var outcome = result.ReadyToRestart
            ? new RestartDrainOutcome(RestartDrainVerdict.Drained, workspaceId,
                "every session reached a clean stop and is verified gone.")
            : new RestartDrainOutcome(RestartDrainVerdict.Blocked, workspaceId,
                result.NotReadyReason ?? "the drain ended not ready to restart and gave no reason.");
        FileLog.Write($"[DirectorDrainRestartStep] RunAsync: request={order.RequestId}, verdict={outcome.Verdict}, workspace={workspaceId}, detail={outcome.Detail}");
        return outcome;
    }

    /// <summary>One progress change as one sentence, in the order a reader wants it: what the drain is
    /// doing, how far it has got, and the most recent thing worth saying.</summary>
    private static string Sentence(DrainProgress p)
    {
        var sentence = $"drain {p.Phase}: {p.Accounted} of {p.Seats} sessions accounted for, {p.Closed} closed";
        return string.IsNullOrWhiteSpace(p.Note) ? sentence + "." : $"{sentence} - {p.Note}";
    }
}
