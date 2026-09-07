using CcDirector.Core.Instances;
using CcDirector.Core.Lifecycle;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Launcher;

/// <summary>
/// What THIS launcher build tells the Gateway it can honour, and the two local facts only this process
/// can see: whether its restart signal is armed, and which storage root it is serving.
///
/// WHY A LAUNCHER IS ASKED INSTEAD OF INSPECTED. Until now the only way to know what a launcher could be
/// told was to compare its version against the release that added the feature. That question is
/// answerable from outside, which is its whole appeal, and it is the fragile form: it breaks on a fork,
/// on a development build, on a build made by hand, and on any build whose version does not track the
/// feature - and it breaks silently, because a version string always parses into something. A launcher
/// that answers for itself cannot be wrong about itself.
///
/// THE LIST IS LOAD-BEARING, NOT A DESCRIPTION OF THE LIST NEXT DOOR. <see cref="Honours"/> is the gate
/// <see cref="LauncherStreamClient"/> runs before it dispatches anything, so a verb absent from
/// <see cref="Verbs"/> is not merely undeclared - it is not executed. That is deliberate. The first draft
/// of this had the tokens in one array and the dispatch in a switch beside it, which is two spellings of
/// one fact, and two spellings agree until the day one of them is edited. Here there is nothing to keep
/// in step: the declaration IS the gate.
///
/// UNDER-DECLARING IS THE SAFE DIRECTION AND OVER-DECLARING IS THE DANGEROUS ONE. A verb this launcher
/// can do and does not declare simply goes unused - the Gateway reports the capability as unavailable and
/// no caller relies on it. A verb it DECLARES and cannot do is a promise a caller acts on, and the gate
/// above cannot catch that one, because the gate only ever removes verbs. So the switch in
/// <see cref="LauncherStreamClient"/> answers a declared-but-unhandled verb with a loud, specific fault
/// naming this class, rather than the generic "unknown verb" that would read as a caller's mistake.
/// </summary>
internal static class LauncherDeclaredCapabilities
{
    /// <summary>
    /// The command verbs this build dispatches. A verb outside this set is refused before dispatch.
    ///
    /// Ordinal-ignore-case, because a token is an identifier one process writes and another reads, and a
    /// capability quietly missed over letter case would read as a launcher that cannot do the thing.
    /// </summary>
    public static readonly IReadOnlyList<string> Verbs = new[]
    {
        LauncherCapabilities.DirectorStart,
        LauncherCapabilities.DirectorStop,
        LauncherCapabilities.DirectorRestart,
        LauncherCapabilities.Launch,
        LauncherCapabilities.Apps,
        LauncherCapabilities.Files,
    };

    /// <summary>
    /// Promises about HOW a verb behaves, rather than verbs of their own - so they are declared to the
    /// Gateway and are deliberately NOT accepted by <see cref="Honours"/>, which gates dispatch. A
    /// condition token arriving as a verb is a caller error and is refused like any other unknown verb.
    ///
    /// EMPTY TODAY, AND THAT IS THE HONEST ANSWER RATHER THAN A GAP. The first condition is
    /// <see cref="LauncherCapabilities.DirectorRestartOnlyIfEmpty"/> - "I will refuse a restart while my
    /// Director still holds live sessions" - and this build's dispatch does not honour it yet, so
    /// declaring it would be exactly the over-declaration described above: a promise a drain would act
    /// on. The token belongs here on the same commit that makes the dispatch honour it, and not before.
    /// </summary>
    public static readonly IReadOnlyList<string> Conditions = Array.Empty<string>();

    /// <summary>Is <paramref name="verb"/> one this build dispatches? The gate, not a description of one.</summary>
    public static bool Honours(string? verb)
        => !string.IsNullOrWhiteSpace(verb)
           && Verbs.Any(v => string.Equals(v, verb, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Build the declaration to send on Hello.
    ///
    /// READ FRESH EVERY TIME, NEVER CACHED. Hello is sent on every connect and every reconnect, and the
    /// signal state can differ between them: signals are armed at startup and their failure is
    /// deliberately non-fatal, so a launcher whose signals never came up is still running and still
    /// streaming. A declaration captured once at construction would keep reporting the state at that
    /// moment for the life of the process.
    ///
    /// THE ROOT IS THIS PROCESS'S OWN, WHICH IS THE POINT. It is read exactly the way the launcher's
    /// signals and registration read it, so a launcher that inherited a Director's CC_DIRECTOR_ROOT
    /// declares the root it is ACTUALLY serving - and the Gateway can then say so. A declaration
    /// computed from where the root was SUPPOSED to be would report the healthy answer for precisely
    /// the machine state that is broken.
    /// </summary>
    public static LauncherCapabilityDeclaration Describe()
    {
        var declaration = new LauncherCapabilityDeclaration
        {
            Commands = Verbs.Concat(Conditions).ToList(),
            ServingRootKey = LifecycleSignalNames.RootKey(),
            ServingRootIsInstanceHome = InstanceContext.LooksLikeAnInstanceHome(InstanceContext.SharedRoot),

            // ASKED, NEVER RAISED. HasListener opens the named handle and closes it; raising the signal
            // instead would restart this launcher's Director as a side effect of describing itself. It is
            // a tri-state: null means the platform cannot be asked, and that null travels all the way to
            // the verdict rather than being flattened into a yes or a no here.
            RestartSignalArmed = LifecycleSignal.HasListener(LifecycleSignalNames.LauncherRestartDirector()),
        };

        FileLog.Write($"[LauncherDeclaredCapabilities] declaring {declaration.Commands.Count} capabilities; "
                      + $"rootKey={declaration.ServingRootKey}, "
                      + $"rootIsInstanceHome={declaration.ServingRootIsInstanceHome}, "
                      + $"restartSignalArmed={Describe(declaration.RestartSignalArmed)}");
        return declaration;
    }

    private static string Describe(bool? armed) => armed switch
    {
        true => "yes",
        false => "NO - this launcher cannot be asked locally to restart its Director",
        null => "not observable on this platform",
    };
}
