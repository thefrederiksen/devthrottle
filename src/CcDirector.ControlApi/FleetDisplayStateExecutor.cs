using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The DISPLAY-STATE area of the Director's tunnel command surface - one verb, <c>set-display-state</c>,
/// through which the Gateway tells this Director the FOLDED display state (effective color, label, triage
/// bucket, needs-you-since, the snooze clock, the snooze-ended marker) of one of its sessions.
///
/// Like <see cref="FleetRoleExecutor"/> this verb is the Gateway telling the Director a FACT, not asking it
/// to DO something. The desktop rail is the one screen that folds from the in-process Session and cannot ask
/// the Gateway for itself, so it re-derived a colour and a label from local facts it could not see -
/// dictation, transcription, voice generation, the snooze clock - and disagreed with every other surface (a
/// snoozed session read red "Needs you" on the rail while the phone and the Cockpit read "Snoozed"). The
/// Gateway is the single fold; this seam carries its answer down to the rail. The Director stores the fold
/// verbatim on <c>Session.Gateway*</c> and reads it back out through <c>ControlEndpoints.Map</c>; it never
/// computes or second-guesses the fold.
///
/// Its own area because a verb belongs to exactly one area and an area is the unit that avoids the merge
/// chokepoint - adding this verb touched no other area's file.
/// (docs/new_architecture/sessions.html.)
/// </summary>
internal sealed class FleetDisplayStateExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        "set-display-state",
    };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return Task.FromResult(command.Verb switch
        {
            "set-display-state" => SetDisplayState(context, command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the fleet display-state area"),
        });
    }

    /// <summary>
    /// The <c>set-display-state</c> verb: store the Gateway's folded display state for one session.
    ///
    /// A blank <see cref="SetDisplayStateRequest.EffectiveColor"/> CLEARS the stamp (back to "no answer")
    /// rather than being rejected - that is how a Director with no Gateway falls back to its neutral
    /// waiting placeholder, and it must be expressible. Nothing here is validated against the fold's
    /// vocabulary: the Gateway is the authority on what the fold says, and a Director second-guessing it
    /// would be the Director deciding a colour, which the law forbids.
    /// </summary>
    internal static DirectorCommandResult SetDisplayState(SessionCommandContext context, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<SetDisplayStateRequest>(command.PayloadJson);
        if (request is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "display-state payload is required");

        var session = context.SessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.ApplyGatewayDisplayState(
            request.EffectiveColor,
            request.StateLabel,
            request.TriageBucket,
            request.NeedsYouSince,
            request.SnoozeUntil,
            request.SnoozeExpired,
            request.InboxLine);

        // Reconcile the raw hold mirror on THIS reliable, change-gated channel - not only on the one-shot
        // hold mirror the Gateway fires alongside its own edges. The Gateway folds a real HoldState every
        // pass, so applying it here means the desktop's Session.OnHold (which still drives the rail's
        // Snooze-versus-Unsnooze menu) self-heals even when that fire-and-forget mirror is dropped or arrives
        // out of order. A blank/unrecognised value (an older Gateway that does not send the field) normalises
        // to null and leaves the existing mirror untouched - never a forced None.
        switch (HoldStates.Normalize(request.HoldState))
        {
            case HoldStates.Held: session.ApplyGatewayHold(HoldState.Held); break;
            case HoldStates.DeferredHold: session.ApplyGatewayHold(HoldState.DeferredHold); break;
            case HoldStates.None: session.ApplyGatewayHold(HoldState.None); break;
        }

        FileLog.Write($"[FleetDisplayStateExecutor] set-display-state: session={guid}, color={request.EffectiveColor ?? "(cleared)"}, label={request.StateLabel ?? "(none)"}, inboxLine={request.InboxLine ?? "(none)"}, hold={request.HoldState ?? "(unchanged)"}");
        return DirectorCommandResult.Success();
    }
}
