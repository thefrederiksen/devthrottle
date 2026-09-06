using CcDirector.Core.Configuration;
using CcDirector.Core.Lifecycle;
using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>Whether the launcher's local command surface - the named lifecycle signal it listens on -
/// is there, is absent, or cannot be observed at all on this platform.</summary>
public enum LauncherCommandSurface
{
    /// <summary>A listener answers for the launcher's restart-the-Director signal. This launcher can be told things.</summary>
    Present,

    /// <summary>Nothing is listening. A launcher build that predates the command surface reads as this, and so does one whose signals failed to start.</summary>
    Absent,

    /// <summary>This platform cannot be asked. See <see cref="LifecycleSignal.HasListener"/> - a Unix listener leaves nothing to consult.</summary>
    NotObservable,
}

/// <summary>
/// One reading of the launcher serving a storage root: what its registration says, whether that
/// process is alive, and whether it holds a command surface.
/// </summary>
/// <param name="Detail">One plain sentence, for a log or a decision record.</param>
public sealed record LauncherWitnessReading(
    bool Registered, bool ProcessAlive, int Pid, string? Version,
    LauncherCommandSurface CommandSurface, string Detail)
{
    /// <summary>
    /// The whole point of this class: a launcher counts as WITNESSED only when a live process is
    /// registered AND its command surface is not absent. A process that started is not evidence of
    /// anything - the failure this exists to catch looks exactly like a healthy launcher from the
    /// outside.
    /// </summary>
    public bool Witnessed => Registered && ProcessAlive && CommandSurface != LauncherCommandSurface.Absent;
}

/// <summary>
/// Reads what can be known, on this machine and with no network, about the launcher serving a storage
/// root: the registration file the running launcher writes about itself, and whether it is listening
/// for the lifecycle signal that tells it to restart the Director.
///
/// WHY THE PROCESS EXISTING IS NOT THE ANSWER. On 2026-09-06 a Director on this account could not be
/// restarted by any route, and it was found out only after seventeen sessions had been drained. The
/// launcher was 1.9.8 - two days older than the code that lets a launcher be told anything at all. It
/// was running, it was registered, it was heartbeating to the Gateway, and it could not receive a
/// single command. Every check that asks "is a launcher up" says yes about that machine. So the
/// question here is never "did something start", it is "is the thing that started able to be
/// commanded", and the evidence is a listener that answers.
///
/// WHY THE GATEWAY IS NOT CONSULTED. The launcher's command stream runs to the Gateway, so it is
/// tempting to make "stream connected" the witness. It must not be. A launcher on a machine with no
/// Gateway configured, or with the network down, is still a perfectly healthy launcher - and a witness
/// that failed there would roll a good build back for a reason that has nothing to do with the build.
/// This reads the two facts that are true locally and are true offline. Whether the stream is
/// CONNECTED is a different question with a different answer and a different fix, and it belongs to
/// the capability query that reports NotConnected apart from NotStreamCapable.
///
/// THE ROOT IS THE SHARED ONE, NEVER THE CALLING PROCESS'S OWN. A Director redirects its whole data
/// tree to its instance home, so <see cref="LauncherDiscovery.DefaultPath"/> read from inside a
/// Director points at <c>instances/&lt;slug&gt;/config/launcher/launcher.json</c> - a file no launcher
/// has ever written. It would report "no launcher installed" on a machine whose launcher is running
/// perfectly, every single time. The shared root is passed in for exactly this reason and the signal
/// name is derived from the same value.
/// </summary>
public sealed class LauncherWitness
{
    private readonly string _sharedRoot;

    public LauncherWitness(string sharedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        _sharedRoot = sharedRoot;
    }

    /// <summary>The registration file the launcher serving this root writes about itself.</summary>
    public static string RegistrationPathFor(string sharedRoot)
        => Path.Combine(sharedRoot, "config", "launcher", "launcher.json");

    /// <summary>Where this witness reads the registration from. Overridable for tests.</summary>
    public string RegistrationPath { get; init; } = "";

    /// <summary>Is this process id alive? Test seam; production asks the operating system.</summary>
    public Func<int, bool>? ProcessIsAlive { get; init; }

    /// <summary>Does this named signal have a listener? Test seam; production asks the operating system.</summary>
    public Func<string, bool?> HasListener { get; init; } = LifecycleSignal.HasListener;

    /// <summary>Every cc-launcher process on the machine, for telling "no launcher" from "a launcher
    /// serving somebody else's root". Test seam; production asks the operating system.</summary>
    public Func<IReadOnlyList<LauncherProcess>> ListLauncherProcesses { get; init; } = InstalledLauncherProcesses.List;

    /// <summary>The signal whose listener proves this launcher can be commanded at all.</summary>
    public string CommandSignalName => LifecycleSignalNames.LauncherRestartDirector(_sharedRoot);

    /// <summary>Read the launcher fact now. Never throws and never caches - a launcher starts and stops
    /// while a Director runs.</summary>
    public LauncherWitnessReading Read()
    {
        var path = string.IsNullOrWhiteSpace(RegistrationPath) ? RegistrationPathFor(_sharedRoot) : RegistrationPath;
        var health = LauncherHealthProbe.ReadRegistration(path, ProcessIsAlive);

        var surface = HasListener(CommandSignalName) switch
        {
            true => LauncherCommandSurface.Present,
            false => LauncherCommandSurface.Absent,
            null => LauncherCommandSurface.NotObservable,
        };

        if (health is null)
            return new LauncherWitnessReading(false, false, 0, null, surface,
                $"no launcher has registered at {path}{ServingAnotherRoot()}");

        var version = health.Version ?? "unknown";
        var detail = health switch
        {
            { Ok: false } => $"the registration at {path} names process {health.Pid}, which is not running"
                              + ServingAnotherRoot(),
            _ when surface == LauncherCommandSurface.Absent =>
                $"launcher {version} is running as process {health.Pid} but nothing is listening for "
                + $"{CommandSignalName}, so it cannot be told anything",
            _ when surface == LauncherCommandSurface.NotObservable =>
                $"launcher {version} is running as process {health.Pid}; whether it is listening for "
                + $"{CommandSignalName} cannot be observed on this platform",
            _ => $"launcher {version} is running as process {health.Pid} and is listening for {CommandSignalName}",
        };

        return new LauncherWitnessReading(true, health.Ok, health.Pid, health.Version, surface, detail);
    }

    /// <summary>
    /// The sentence that turns "no launcher here" into the reason, when a launcher IS running from this
    /// install and has registered somewhere else.
    ///
    /// This is a real machine state and it was invisible: on 2026-09-06 a launcher was started by hand
    /// from a shell that had inherited a Director's <c>CC_DIRECTOR_ROOT</c>, so it took that Director's
    /// INSTANCE HOME for the machine root. It came up perfectly - registered, heartbeating, stream
    /// connected, both lifecycle signals armed - and every one of those facts was filed under the wrong
    /// root: its registration went to <c>instances/&lt;slug&gt;/config/launcher</c> and its signals were
    /// named for that root, so nothing computing from the real root could see or reach it. Read without
    /// this, the machine says "no launcher", which sends the next person looking for a process that is
    /// plainly running.
    /// </summary>
    private string ServingAnotherRoot()
    {
        try
        {
            var running = InstalledLauncherProcesses.Ours(
                Path.Combine(_sharedRoot, "launcher"), ListLauncherProcesses());
            if (running.Count == 0) return "";

            var pids = string.Join(", ", running.Select(p => p.Pid));
            return $" - but launcher process(es) {pids} ARE running from this install, so they are serving a "
                   + "different storage root: their registration and their signal names are keyed to that "
                   + "root and nothing here can reach them";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherWitness] could not list launcher processes: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// The version of the launcher that is BOTH running and commandable, or null when there is no such
    /// launcher. This is the shape an apply's health poll wants: null and "a version that is not the
    /// one we installed" are both "not proved yet", and only a proved new version ends the wait.
    /// </summary>
    public string? WitnessedVersion()
    {
        var reading = Read();
        return reading.Witnessed ? reading.Version : null;
    }
}
