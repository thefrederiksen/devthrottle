using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>How far a launcher is from being able to receive a command at all.</summary>
/// <remarks>
/// SERIALISED AS ITS NAME, NEVER ITS NUMBER. Without this the wire carries <c>"verdict": 1</c>, and a
/// client cannot render that - it would have to hold its own copy of the ordering and re-derive what the
/// value means, which is the dumb-client rule broken in the one place it matters most. Worse, the
/// numbers are POSITIONAL: inserting a state in the middle silently re-labels every stored or logged
/// answer that came before. Found by the end-to-end proof on its first run, where every reason sentence
/// was correct and every enum came back as a digit.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LauncherReach
{
    /// <summary>This tenant has no launcher registered for that machine name. Install or start one.</summary>
    NoLauncher,

    /// <summary>Registered, but the heartbeat has gone quiet AND it holds no stream: it has stopped talking
    /// to this Gateway altogether - crashed, stopped, or cut off. Get that launcher running again.</summary>
    NotConnected,

    /// <summary>Registered AND heartbeating, yet holding no command stream: alive, reaching this Gateway,
    /// and unable to receive anything from it. That is what a launcher older than the command stream looks
    /// like. Update the launcher. ITS NETWORK IS NOT THE PROBLEM, which is exactly why this may never share
    /// a message with <see cref="NotConnected"/>.</summary>
    NotStreamCapable,

    /// <summary>The launcher holds a live command stream. A command can be delivered.</summary>
    Connected,
}

/// <summary>
/// Whether the launcher said anything about itself when it joined - and the two ways "it did not" can
/// happen, which are different faults with different fixes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LauncherDeclarationState
{
    /// <summary>No Hello has been received for this machine, so nothing was ever declared. WE NEVER GOT TO
    /// ASK. Reading this as "too old" would certify a machine that was merely unreachable - the
    /// absence-shaped mistake this enum exists to prevent.</summary>
    NotDeclared,

    /// <summary>A Hello arrived and carried NO declaration. The launcher is reachable and is older than the
    /// capability handshake. Update it, and it will start answering for itself.</summary>
    DeclaredNothing,

    /// <summary>A Hello arrived carrying a declaration. What this launcher honours is known from its own
    /// word rather than inferred from a version number.</summary>
    Declared,
}

/// <summary>The state of the local lifecycle signal that asks a launcher to restart its Director.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RestartSignalState
{
    /// <summary>The launcher did not say. Either no declaration arrived at all, or one arrived from a build
    /// that does not report it.</summary>
    NotDeclared,

    /// <summary>The launcher reports the signal armed. A Director on that machine can ask its own launcher
    /// to restart it with no network at all.</summary>
    Listening,

    /// <summary>The launcher reports nothing listening. It cannot be asked locally - this is the shape of a
    /// build that predates lifecycle signals, and of one whose signals failed to start.</summary>
    NotListening,

    /// <summary>The launcher reports that its platform cannot be asked. Off Windows a listener leaves
    /// nothing to consult, so this is an honest unknown and travels to the verdict as one.</summary>
    NotObservable,
}

/// <summary>Whether a promise holds, does not hold, or is not knowable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilityState
{
    /// <summary>Definitely available - the launcher declared it.</summary>
    Available,

    /// <summary>Definitely NOT available - the launcher declared its capabilities and this is not among
    /// them.</summary>
    Unavailable,

    /// <summary>Not knowable from here. Either nothing was declared, or the launcher cannot be reached to
    /// ask. A caller must treat this as a refusal to proceed, never as a yes.</summary>
    Unknown,
}

/// <summary>The plain answer to "can this machine be restarted?".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RestartVerdict
{
    /// <summary>Yes, and <see cref="MachineRestartCapabilityDto.Reason"/> names the route.</summary>
    CanRestart,

    /// <summary>No, and the reason says which fault and what to do about it.</summary>
    CannotRestart,

    /// <summary>Not knowable. The only candidate route's state cannot be observed. NOT A YES: a caller
    /// proceeds only on <see cref="CanRestart"/>.</summary>
    Unknown,
}

/// <summary>
/// Everything known about whether ONE machine can complete a Director restart - asked BEFORE anything is
/// sent to it, and without raising any signal.
///
/// WHY THIS EXISTS. On 2026-09-06 a Director on this account was drained of seventeen sessions and only
/// then did it turn out that no route could restart it: the installed launcher was 1.9.8, two days older
/// than the code that lets a launcher be told anything at all. Every liveness check on the machine said
/// yes. The launcher was running, registered and heartbeating, and could not receive a single command. A
/// drain that cannot end in a restart is a fleet-wide close with paperwork, so the machine has to say so
/// first.
///
/// NOTHING HERE HAS A CONSEQUENCE. The one other way to find out whether a launcher is listening for the
/// restart signal is to RAISE it, and raising it restarts a Director as a side effect of the question. So
/// the signal is reported from what the launcher declared about itself, never by probing it.
///
/// TWO SEPARATE QUESTIONS, AND CONFLATING THEM WOULD MAKE BOTH USELESS. <see cref="Verdict"/> answers
/// "can a restart be delivered at all". <see cref="GuardedRestart"/> answers "and will the launcher refuse
/// it while the Director still holds live sessions". A machine can be perfectly restartable and offer no
/// guard - which is the state of every launcher shipped before the guard existed - so folding the guard
/// into the verdict would report every machine in the fleet as unrestartable, and folding it out of the
/// answer entirely would let a drain send a guarded restart to a launcher that cannot see the guard.
/// </summary>
public sealed class MachineRestartCapabilityDto
{
    /// <summary>The machine this answer is about, as the caller named it.</summary>
    public string Machine { get; set; } = "";

    /// <summary>The launcher's registered version, or null when no launcher is registered. Reported as
    /// EVIDENCE for the reason, never as the thing the verdict was computed from.</summary>
    public string? LauncherVersion { get; set; }

    /// <summary>Seconds since that launcher last registered or heartbeated. 0 when none is registered. It
    /// is the fact that separates "too old to stream" from "stopped talking", so it travels on the
    /// answer.</summary>
    public int QuietForSeconds { get; set; }

    /// <summary>How far the launcher is from being able to receive a command.</summary>
    public LauncherReach Reach { get; set; }

    /// <summary>Whether the launcher declared anything about itself, and which way it did not.</summary>
    public LauncherDeclarationState Declaration { get; set; }

    /// <summary>The capability tokens the launcher declared, in the order it declared them. Empty when it
    /// declared nothing or was never reached - and those two cases are told apart by
    /// <see cref="Declaration"/>, never by this list being empty.</summary>
    public List<string> DeclaredCommands { get; set; } = new();

    /// <summary>The local restart signal's state, as the launcher reported it.</summary>
    public RestartSignalState RestartSignal { get; set; }

    /// <summary>True when the launcher reported that the root it is serving is a Director's instance home
    /// rather than the machine's shared root - a launcher that looks entirely healthy and is invisible to
    /// every signal computed from the real root. Null when it did not say.</summary>
    public bool? ServingRootIsInstanceHome { get; set; }

    /// <summary>The short key of the storage root the launcher is serving, when it said. No path.</summary>
    public string? ServingRootKey { get; set; }

    /// <summary>Can a restart be delivered to this machine?</summary>
    public RestartVerdict Verdict { get; set; }

    /// <summary>
    /// One plain sentence: the verdict's reason, naming the route when it is yes and the fix when it is no.
    /// It is written on the Gateway and rendered verbatim - a client never re-derives it. Plain ASCII, so a
    /// terminal and a log can both print it.
    /// </summary>
    public string Reason { get; set; } = "";

    /// <summary>
    /// Will this launcher REFUSE a restart while its Director still holds live sessions?
    ///
    /// <see cref="CapabilityState.Unknown"/> is the answer for every launcher that declared nothing, and a
    /// drain must read it as "do not send one", not as "probably fine". The whole point of asking before
    /// sending is that the alternative - send it, and read the answer - has already restarted the Director
    /// by the time the answer arrives.
    /// </summary>
    public CapabilityState GuardedRestart { get; set; }

    /// <summary>The reason behind <see cref="GuardedRestart"/>, in the same plain-sentence form as
    /// <see cref="Reason"/>. Separate, because a machine that can be restarted but offers no guard has two
    /// true things to say and one message cannot carry both.</summary>
    public string GuardedRestartReason { get; set; } = "";
}
