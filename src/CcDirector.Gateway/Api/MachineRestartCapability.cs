using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE ONE PLACE the answer to "can this machine be restarted?" is computed. A pure fold over the facts
/// the Gateway holds and the facts the machine's launcher declared about itself, producing the finished
/// verdict, the finished reason sentence, and every fact the reason was drawn from.
///
/// WHY IT EXISTS AT ALL. On 2026-09-06 a Director on this account was drained of seventeen sessions and
/// only then did it turn out that nothing could restart it. The installed launcher was 1.9.8 - two days
/// older than the code that lets a launcher be told anything - and it was running, registered and
/// heartbeating the entire time. Every liveness check on the machine said yes. A drain that cannot end in
/// a restart is a fleet-wide close with paperwork, so the machine has to answer first, and the answer has
/// to be asked for BEFORE the first session is closed.
///
/// THE CLIENT IS DUMB. The verdict, the route states and both sentences are folded here and rendered
/// verbatim by whatever reads them - a command line, a drain, the request route Phase 6 adds. Nothing
/// downstream re-derives what a state means. A caller that branched on the enums to write its own
/// sentence would, the first time it met a combination it did not expect, print something PLAUSIBLE
/// instead of something TRUE.
///
/// NOTHING HERE HAS A CONSEQUENCE. The only other way to learn whether a launcher is listening for the
/// restart signal is to RAISE it, and raising it restarts a Director as a side effect of the question. So
/// the signal is read from what the launcher declared about itself when it joined, and no signal is ever
/// touched. A capability check may not have consequences.
///
/// WHY DECLARED RATHER THAN INFERRED FROM THE VERSION. A version string tells you which release a build
/// came from, and the question is what THIS build honours. The two agree only while nobody forks,
/// nobody builds by hand, and every feature lands in the release its number implies - and when they stop
/// agreeing, a version check does not fail, it answers confidently and wrongly. A launcher that answers
/// for itself cannot be wrong about itself.
/// </summary>
internal static class MachineRestartCapability
{
    /// <summary>
    /// The state of one route by which a restart could reach the Director on this machine, with the
    /// sentence that says why.
    /// </summary>
    private readonly record struct Route(CapabilityState State, string Reason);

    /// <summary>
    /// Fold the facts into the answer.
    ///
    /// <paramref name="connection"/> IS THE SINGLE SOURCE FOR BOTH "IS IT STREAMING" AND "WHAT DID IT
    /// DECLARE", and that is structural rather than tidy: the two used to be separate arguments, and a
    /// caller could then hand over a declaration alongside no connection - a launcher that has gone,
    /// still vouching for itself out of a stale record. One nullable object cannot express that state.
    /// </summary>
    /// <param name="machine">The machine name as the caller wrote it, echoed back on the answer.</param>
    /// <param name="registered">That tenant's launcher registration row, or null when there is none.</param>
    /// <param name="connection">The launcher's live command stream and what it declared on joining, or
    /// null when it holds no stream.</param>
    /// <param name="nowUtc">The clock, injected so the heartbeat window is testable.</param>
    public static MachineRestartCapabilityDto Judge(
        string machine,
        LauncherDto? registered,
        Streaming.LauncherStreamConnection? connection,
        DateTime nowUtc)
    {
        var reach = LauncherReachability.Classify(registered, connection is not null, nowUtc);
        var declaration = connection?.Declaration;

        var declarationState = connection is null
            ? LauncherDeclarationState.NotDeclared
            : declaration is null
                ? LauncherDeclarationState.DeclaredNothing
                : LauncherDeclarationState.Declared;

        var commands = declaration?.Commands is { Count: > 0 } declared
            ? declared.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList()
            : new List<string>();

        var signal = declaration is null
            ? RestartSignalState.NotDeclared
            : declaration.RestartSignalArmed switch
            {
                true => RestartSignalState.Listening,
                false => RestartSignalState.NotListening,
                null => RestartSignalState.NotObservable,
            };

        var dto = new MachineRestartCapabilityDto
        {
            Machine = machine,
            LauncherVersion = registered?.Version,
            QuietForSeconds = LauncherReachability.QuietForSeconds(registered, nowUtc),
            Reach = reach,
            Declaration = declarationState,
            DeclaredCommands = commands,
            RestartSignal = signal,
            ServingRootIsInstanceHome = declaration?.ServingRootIsInstanceHome,
            ServingRootKey = declaration?.ServingRootKey,
        };

        var (verdict, reason) = Decide(dto, commands);
        dto.Verdict = verdict;
        dto.Reason = reason;

        var (guard, guardReason) = DecideGuardedRestart(dto, commands);
        dto.GuardedRestart = guard;
        dto.GuardedRestartReason = guardReason;

        return dto;
    }

    /// <summary>
    /// The verdict, from the two routes a restart can travel.
    ///
    /// THE WRONG-ROOT FAULT IS RULED ON FIRST, ahead of every route, because it is the only one that
    /// makes both routes look fine and work on the wrong Director. A launcher serving an instance home
    /// registers, heartbeats, streams and arms its signals - all of it filed under a root nothing else
    /// computes - so every other fact in the answer reads healthy. It is ranked first and not merely
    /// mentioned because a reader who acts on the first sentence must act on THIS one.
    /// </summary>
    private static (RestartVerdict, string) Decide(MachineRestartCapabilityDto dto, List<string> commands)
    {
        if (dto.ServingRootIsInstanceHome == true)
            return (RestartVerdict.CannotRestart,
                $"cannot restart {dto.Machine}: its launcher (version {Version(dto)}) is serving a Director's "
                + $"instance home rather than the machine's shared root{RootKey(dto)}, so its registration and "
                + "its signal names are keyed to that root and nothing computing from the real root can reach "
                + "it. It looks healthy from every other angle and can restart nothing the fleet is using. "
                + "Restart that launcher from a shell with no CC_DIRECTOR_ROOT set.");

        var stream = StreamRoute(dto, commands);
        var local = LocalSignalRoute(dto);

        if (stream.State == CapabilityState.Available)
            return (RestartVerdict.CanRestart, stream.Reason);
        if (local.State == CapabilityState.Available)
            return (RestartVerdict.CanRestart, local.Reason);

        // No route is available. An UNKNOWN among them is not a no - it is a question this platform or
        // this launcher cannot answer, and a caller must be told which of the two it is holding rather
        // than being handed a confident refusal it cannot check.
        if (stream.State == CapabilityState.Unknown || local.State == CapabilityState.Unknown)
            return (RestartVerdict.Unknown,
                $"cannot tell whether {dto.Machine} can be restarted. {stream.Reason} {local.Reason}");

        return (RestartVerdict.CannotRestart, $"cannot restart {dto.Machine}: {stream.Reason} {local.Reason}");
    }

    /// <summary>
    /// The route a REMOTE caller has: a command pushed down the stream the launcher itself opened.
    ///
    /// A DECLARED-NOTHING LAUNCHER IS AVAILABLE, NOT UNKNOWN, and this is the one inference in the whole
    /// fold. The stream is the delivery path and the launcher is holding one; every launcher that can
    /// open a stream at all has dispatched "director/restart" since the day the stream shipped. So the
    /// stream is itself the evidence, and the reason says plainly that the capability is inferred from it
    /// rather than taken from the launcher's own word - which is a weaker claim, and the reader is told
    /// so instead of having to work it out.
    ///
    /// A launcher that DID declare and left the verb out is Unavailable on its own testimony. That is the
    /// one case where a declaration makes the answer more negative, and it is right: a build that says it
    /// cannot restart a Director should be believed.
    /// </summary>
    private static Route StreamRoute(MachineRestartCapabilityDto dto, List<string> commands)
    {
        switch (dto.Reach)
        {
            case LauncherReach.NoLauncher:
                return new Route(CapabilityState.Unavailable,
                    $"no launcher is registered for '{dto.Machine}' on this account, so there is nothing to "
                    + "ask. Install and start cc-launcher on that machine.");

            case LauncherReach.NotConnected:
                return new Route(CapabilityState.Unavailable,
                    $"its launcher (version {Version(dto)}) is registered but has been silent for "
                    + $"{dto.QuietForSeconds}s and holds no command stream - it has stopped talking to this "
                    + "Gateway altogether. Get that launcher running again and able to reach this Gateway.");

            case LauncherReach.NotStreamCapable:
                // THE 2026-09-06 CASE, WORD FOR WORD. Registered, heartbeating, and unable to receive a
                // thing. Its network is provably working, so it must never be sent the message above.
                return new Route(CapabilityState.Unavailable,
                    $"its launcher (version {Version(dto)}) is registered and heartbeating - it reached this "
                    + $"Gateway {dto.QuietForSeconds}s ago - and yet holds no command stream, so it predates "
                    + "the command stream and cannot be told anything. Update the launcher on that machine. "
                    + "Its network connection is not the problem.");
        }

        if (dto.Declaration == LauncherDeclarationState.DeclaredNothing)
            return new Route(CapabilityState.Available,
                $"{dto.Machine} can be restarted: its launcher (version {Version(dto)}) holds a live command "
                + "stream, which is the only path a restart travels, so one can be delivered. That launcher "
                + "declares no capabilities of its own - it predates the capability handshake - so this is "
                + "inferred from the stream rather than taken from the launcher's own word. Update it to get "
                + "a declared answer.");

        if (!Declares(commands, LauncherCapabilities.DirectorRestart))
            return new Route(CapabilityState.Unavailable,
                $"its launcher (version {Version(dto)}) holds a live command stream and declares that it does "
                + $"not offer a Director restart (it declares: {Commands(commands)}). Update or replace the "
                + "launcher on that machine with a build that does.");

        return new Route(CapabilityState.Available,
            $"{dto.Machine} can be restarted: its launcher (version {Version(dto)}) holds a live command "
            + $"stream and declares {LauncherCapabilities.DirectorRestart}.");
    }

    /// <summary>
    /// The route a process ON THAT MACHINE has: raise the named lifecycle signal, with no network of any
    /// kind. It is the route a drained Director uses to ask its own launcher to restart it.
    ///
    /// WHEN NOTHING WAS DECLARED THIS IS UNAVAILABLE, NOT UNKNOWN, and the distinction is worth the
    /// paragraph. A launcher reports its signal state in the declaration, and the declaration rides the
    /// stream - so a launcher with no stream cannot report it, ever. That is not a fact we are briefly
    /// missing and might get later by asking harder: there is no other channel. Calling it Unknown would
    /// make the 2026-09-06 machine answer "cannot tell", when the correct answer - the one the whole phase
    /// exists to produce - is a plain no with the reason "update the launcher". A route whose availability
    /// can never be established is not a route a drain can be planned around.
    ///
    /// NotObservable IS a genuine unknown and stays one: the launcher answered, and its answer was that
    /// its platform cannot be asked. That unknown reaches the verdict rather than being resolved into a
    /// guess either way.
    /// </summary>
    private static Route LocalSignalRoute(MachineRestartCapabilityDto dto) => dto.RestartSignal switch
    {
        RestartSignalState.Listening => new Route(CapabilityState.Available,
            $"{dto.Machine} can be restarted: its launcher (version {Version(dto)}) is listening for the "
            + "local restart signal, so a process on that machine - a Director asking its own launcher - can "
            + "ask for one with no network at all."),

        RestartSignalState.NotListening => new Route(CapabilityState.Unavailable,
            "Nothing is listening for the local restart signal on that machine either, so a Director there "
            + "cannot ask its own launcher for a restart. Restarting the launcher arms it."),

        RestartSignalState.NotObservable => new Route(CapabilityState.Unknown,
            "Whether anything is listening for the local restart signal cannot be observed on that "
            + "machine's platform, so that route can be neither confirmed nor ruled out."),

        _ => new Route(CapabilityState.Unavailable,
            "Its local restart signal cannot be reported either: a launcher declares that only over the "
            + "command stream it is not holding."),
    };

    /// <summary>
    /// Whether the launcher will REFUSE a restart while its Director still holds live sessions.
    ///
    /// ASKED SEPARATELY FROM THE VERDICT, AND IT HAS TO BE. A machine can be perfectly restartable and
    /// offer no such guard - that describes every launcher built before the guard existed, which today is
    /// all of them - so folding the guard into the verdict would report the whole fleet as unrestartable.
    /// Leaving it off the answer entirely is the opposite failure and the more expensive one: a drain
    /// would send a guarded restart to a launcher that cannot see the guard, that launcher would restart a
    /// half-drained Director and report success, and the remaining sessions would go with it.
    ///
    /// UNKNOWN IS A REFUSAL TO PROCEED, NOT A PROBABLY-FINE. The alternative to asking in advance is to
    /// send the restart and read the answer - and by the time that answer comes back the Director has
    /// already been restarted. This is the detection-versus-prevention seam, and it is the reason the
    /// declaration exists at all rather than a version comparison.
    /// </summary>
    private static (CapabilityState, string) DecideGuardedRestart(
        MachineRestartCapabilityDto dto, List<string> commands)
    {
        if (dto.Declaration == LauncherDeclarationState.NotDeclared)
            return (CapabilityState.Unknown,
                $"whether the launcher on {dto.Machine} would refuse a restart while its Director still holds "
                + "live sessions is unknown: it holds no command stream, so it has declared nothing. Do not "
                + "send a guarded restart - an older launcher ignores the condition, restarts anyway and "
                + "reports success.");

        if (dto.Declaration == LauncherDeclarationState.DeclaredNothing)
            return (CapabilityState.Unknown,
                $"whether the launcher on {dto.Machine} (version {Version(dto)}) would refuse a restart while "
                + "its Director still holds live sessions is unknown: it declares no capabilities at all, so "
                + "it predates the capability handshake. It is NOT the same as a launcher that answered and "
                + "said no. Update it, and it will say either way.");

        if (!Declares(commands, LauncherCapabilities.DirectorRestartOnlyIfEmpty))
            return (CapabilityState.Unavailable,
                $"the launcher on {dto.Machine} (version {Version(dto)}) declares that it does NOT offer a "
                + "guarded restart, so a restart sent to it would go ahead even with live sessions on that "
                + "Director. Drain it to empty first, or update that launcher.");

        return (CapabilityState.Available,
            $"the launcher on {dto.Machine} (version {Version(dto)}) declares "
            + $"{LauncherCapabilities.DirectorRestartOnlyIfEmpty}, so it will refuse a restart while its "
            + "Director still holds live sessions and say how many.");
    }

    /// <summary>Case-insensitive, because a token is an identifier a build writes and a fold reads, and a
    /// capability silently missed over letter case would read as a launcher that cannot do the thing.</summary>
    private static bool Declares(List<string> commands, string token)
        => commands.Any(c => string.Equals(c, token, StringComparison.OrdinalIgnoreCase));

    private static string Version(MachineRestartCapabilityDto dto)
        => string.IsNullOrWhiteSpace(dto.LauncherVersion) ? "unknown" : dto.LauncherVersion;

    private static string RootKey(MachineRestartCapabilityDto dto)
        => string.IsNullOrWhiteSpace(dto.ServingRootKey) ? "" : $" (root key {dto.ServingRootKey})";

    private static string Commands(List<string> commands)
        => commands.Count == 0 ? "nothing" : string.Join(", ", commands);
}
