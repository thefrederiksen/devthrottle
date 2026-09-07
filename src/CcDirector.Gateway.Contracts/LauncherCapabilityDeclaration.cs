namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The capability tokens a launcher may declare when it joins the command stream. Written once, here,
/// so the launcher that declares one and the Gateway that reads it cannot disagree about a string.
///
/// A TOKEN IS A PROMISE ABOUT BEHAVIOUR, NOT A BUILD NUMBER. That is the whole reason this exists.
/// Before it, the only way to work out what a launcher could be asked to do was to compare its version
/// against the release that added the feature - which is the fragile form of the question. It breaks on
/// a fork, on a development build, on a build whose version does not track the feature, and on any
/// build somebody made by hand; and it breaks SILENTLY, because a version string always parses into
/// something. Asking the launcher what it honours cannot be wrong about a launcher that answers.
///
/// A VERB TOKEN AND A CONDITION TOKEN ARE DIFFERENT PROMISES. <see cref="DirectorRestart"/> says "I can
/// restart the Director I supervise". <see cref="DirectorRestartOnlyIfEmpty"/> says "and I will REFUSE to
/// do it while that Director is holding live sessions". The second is a promise about a REFUSAL, and it
/// is the one a drain actually needs: a launcher that ignores the condition restarts a half-drained
/// Director and reports success, so an answer read after the fact is detection, never prevention. A
/// launcher declares the condition token ONLY when its own dispatch honours the condition.
/// </summary>
public static class LauncherCapabilities
{
    /// <summary>Start the Director this launcher supervises.</summary>
    public const string DirectorStart = "director/start";

    /// <summary>Stop the Director this launcher supervises.</summary>
    public const string DirectorStop = "director/stop";

    /// <summary>Restart the Director this launcher supervises.</summary>
    public const string DirectorRestart = "director/restart";

    /// <summary>
    /// Honour a restart that is conditional on the Director being EMPTY: restart when it holds no live
    /// sessions, and refuse - saying how many are live - when it does.
    ///
    /// DECLARED SEPARATELY FROM <see cref="DirectorRestart"/> BECAUSE IT IS A DIFFERENT PROMISE AND
    /// ARRIVED LATER. Every launcher that can restart a Director at all can be asked to restart one
    /// unconditionally; only a launcher carrying the guard can promise to decline. A drain that treated
    /// the two as one would send a guarded restart to a launcher that cannot see the guard, and take
    /// the remaining sessions with it.
    /// </summary>
    public const string DirectorRestartOnlyIfEmpty = "director/restart:only-if-empty";

    /// <summary>Start an arbitrary application on the machine.</summary>
    public const string Launch = "launch";

    /// <summary>Answer with the machine's installed-application catalogue.</summary>
    public const string Apps = "apps";

    /// <summary>Answer with a filename search across the machine's drives.</summary>
    public const string Files = "files";
}

/// <summary>
/// What a launcher says about ITSELF when it joins the Gateway's command stream: which commands it can
/// honour, whether its local restart signal is armed, and which storage root it is serving.
///
/// WHY THE LAUNCHER IS ASKED RATHER THAN INSPECTED. Three of these facts are only knowable on the
/// machine. A named signal's listener lives in one logon session and cannot be observed from a Gateway
/// at all. The storage root a launcher resolved is a decision that launcher made at startup from its own
/// environment. And what a build honours is a property of the build. Every one of them was previously
/// GUESSED from the version string, and the guess is what this replaces.
///
/// EVERY FIELD IS OPTIONAL, AND A MISSING ONE IS NOT A NO. An older launcher sends a Hello with no
/// declaration at all, and that is a THIRD state - reachable, but silent about itself - which must never
/// be read as "cannot". See <see cref="MachineRestartCapabilityDto"/> for how the three states are kept
/// apart, and why collapsing them would certify a machine that was merely unreachable.
///
/// NO PATHS TRAVEL. The root is identified by <see cref="ServingRootKey"/> - the same short hash the
/// lifecycle signal names already embed - and classified on the machine into
/// <see cref="ServingRootIsInstanceHome"/>. A full filesystem path would be machine-private detail
/// leaving the machine to reach a hosted Gateway, for no answer the hash and the flag do not already give.
/// </summary>
public sealed class LauncherCapabilityDeclaration
{
    /// <summary>
    /// The command tokens this launcher can honour - see <see cref="LauncherCapabilities"/>. A launcher
    /// declares exactly what its own dispatch handles; declaring a token it does not honour is worse than
    /// declaring nothing, because a caller would then act on the promise.
    /// </summary>
    public List<string> Commands { get; set; } = new();

    /// <summary>
    /// Is this launcher listening for the lifecycle signal that asks it to restart its Director - the
    /// path that works with no network at all, and the one a Director uses to ask its OWN launcher?
    ///
    /// A TRI-STATE, AND THE NULL IS NOT A HEDGE. On Windows a named event either exists in this logon
    /// session or it does not. Off Windows the mechanism is a request FILE a listener polls, and there is
    /// no registry of listeners to consult - the absence of a file says nothing about whether anybody is
    /// watching for one. Null means NOT OBSERVABLE, and it travels to the verdict as an unknown rather
    /// than being invented into a yes or a no.
    /// </summary>
    public bool? RestartSignalArmed { get; set; }

    /// <summary>
    /// The short, stable key of the storage root this launcher is serving - the same value
    /// <c>LifecycleSignalNames.RootKey</c> derives and embeds in every signal name. Two launchers serving
    /// two roots on one machine have two keys, which is how a test rig and an installed launcher are told
    /// apart without either one naming a path.
    /// </summary>
    public string? ServingRootKey { get; set; }

    /// <summary>
    /// Is the root this launcher is serving actually a Director's INSTANCE HOME rather than the machine's
    /// shared root?
    ///
    /// TRUE IS A FAULT, AND IT IS INVISIBLE FROM EVERY OTHER ANGLE. On 2026-09-06 a launcher on this
    /// account was started from a shell that had inherited a Director's <c>CC_DIRECTOR_ROOT</c>, so it
    /// took that Director's instance home for the machine root. It came up perfectly - registered,
    /// heartbeating, stream connected, both lifecycle signals armed - and every one of those facts was
    /// filed under the wrong root. Its registration went to <c>instances/&lt;slug&gt;/config/launcher</c>
    /// and its signals were named for that root, so nothing computing from the real root could see it or
    /// reach it. A launcher in that state reports a current version, a live stream and an armed signal,
    /// and cannot restart the machine's Director.
    ///
    /// A launcher serving a deliberately isolated root - a test rig - is NOT this state and must not be
    /// refused: its root is its own directory, not an instance home. That is precisely the distinction
    /// this flag draws, and it is why the classification is made on the machine, where the shape of the
    /// path is knowable, rather than inferred from a hash at the Gateway.
    /// </summary>
    public bool? ServingRootIsInstanceHome { get; set; }
}
