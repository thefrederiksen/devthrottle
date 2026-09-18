namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Issue #1176 (Phase 1a): the first message a Director sends after opening its stream to the Gateway,
/// declaring which Director this connection speaks for. The hub binds the connection to this identity;
/// from then on every snapshot/delta/remove from that connection applies only to this Director, so one
/// connection can never push into another Director's cache.
/// </summary>
public sealed class DirectorStreamHello
{
    /// <summary>The stable Director id (the same id used for HTTP registration and the /sessions aggregation).</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Director build version, for diagnostics.</summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// Gateway Cleanup mission (tunnel-only): the Director's machine name. The tunnel is now the ONLY
    /// registration path (HTTP register is gone), so Hello carries the identity the Gateway's registry
    /// needs to know this Director exists - machine, user, pid, start time. The Gateway registers a
    /// tunnel Director with Source="stream" and NO control/tailnet endpoint (it is never dialed).
    /// </summary>
    public string MachineName { get; set; } = "";

    /// <summary>Gateway Cleanup mission (tunnel-only): the OS user the Director runs as (roster display).</summary>
    public string User { get; set; } = "";

    /// <summary>Gateway Cleanup mission (tunnel-only): the Director process id (roster/diagnostics).</summary>
    public int Pid { get; set; }

    /// <summary>
    /// This Director SENDS its sessions' conversations (the turn-push mission). False from a build too old
    /// to have the feature, which is why the Gateway asks rather than assuming: when it holds no
    /// conversation for a session, "it has not arrived yet" and "that computer cannot send it" are
    /// different news to the person looking at an empty Chat screen, and only this tells them apart.
    /// </summary>
    public bool PushesTurns { get; set; }

    /// <summary>
    /// This Director honours <see cref="PromptRequest.OnlyWhenWaitingForInput"/>: it checks the session is waiting for a
    /// prompt, and types, under the input lock the owner's own keystrokes take (the Fleet Manager mission, step 4).
    /// False from a build too old to have the check. The Gateway sends the Fleet Manager's events only to a Director
    /// that says true, because an older one ignores the field and types whatever the session is doing.
    /// </summary>
    public bool ChecksIdleBeforeTyping { get; set; }

    /// <summary>Gateway Cleanup mission (tunnel-only): when the Director process started (UTC).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// devthrottle_internal#1176: the instance's user-editable display name (from the named-instance
    /// registry, e.g. "SOREN_NORTH_SLOT_2"), so several Directors on one machine are tellable apart
    /// in the fleet UI. Empty when the instance has no name (older builds, unnamed default). Cosmetic
    /// only: the Gateway sanitizes it and never keys on it.
    /// </summary>
    public string DisplayName { get; set; } = "";
}
