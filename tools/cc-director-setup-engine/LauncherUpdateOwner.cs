using System.Diagnostics;
using CcDirector.Core.Lifecycle;
using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>A staged launcher build the Director found, and what it must become.</summary>
/// <param name="Version">The version the staged build declares about itself.</param>
/// <param name="StagedBuild">The downloaded build waiting to be installed.</param>
/// <param name="InstallTarget">The path the staged build must become.</param>
public sealed record StagedLauncherUpdate(string Version, string StagedBuild, string InstallTarget);

/// <summary>Why a staged launcher update was not applied on this pass, or that one was.</summary>
public enum LauncherUpdateDecision
{
    /// <summary>Nothing is staged, or what is staged is not newer than the launcher that is installed.</summary>
    NothingStaged,

    /// <summary>A newer build is staged but no launcher is running, and it is not this loop's business to start one.</summary>
    HeldBecauseLauncherNotRunning,

    /// <summary>A newer build is staged but which process the launcher IS could not be established, so nothing was touched.</summary>
    HeldBecauseUndecidable,

    /// <summary>
    /// A newer build is staged, but on this platform a launcher cannot be observed to hold a command
    /// surface at all, so no swap could ever be certified. Nothing was touched. See
    /// <see cref="LauncherWitnessReading.Witnessed"/>: this is Windows-only until the launcher
    /// publishes its own capability.
    /// </summary>
    HeldBecauseTheCommandSurfaceCannotBeObserved,

    /// <summary>
    /// A newer build is staged, but another binary swap is already running on this machine - the
    /// launcher installing the Director's update, or a second Director installing the launcher's.
    /// Nothing was touched; the next pass looks again.
    /// </summary>
    HeldBecauseAnotherSwapIsRunning,

    /// <summary>The staged build is one that already failed to come up and was pinned away from.</summary>
    SkippedPinnedBadVersion,

    /// <summary>The update was applied and the new launcher was witnessed alive and commandable.</summary>
    Applied,

    /// <summary>
    /// The launcher was replaced and witnessed, AND THIS DIRECTOR NO LONGER HOLDS ITS INSTANCE. The
    /// swap stopped a process that on Windows is this Director's parent, so the one thing that had to
    /// stay true did not: either this process was taken with it, or the new launcher started a second
    /// Director over the same home.
    ///
    /// It is its own answer and not an <see cref="Applied"/> with a longer sentence, because a caller
    /// that switches on the decision would otherwise read the worst outcome this class can produce as
    /// a success - and the message that carried the warning is the part nothing switches on.
    /// </summary>
    AppliedButThisDirectorLostItsInstance,

    /// <summary>The update was applied, the new launcher was never witnessed, and the previous build was put back.</summary>
    RolledBack,

    /// <summary>The attempt failed before or during the swap; the message says what was left where.</summary>
    Failed,
}

/// <summary>What one pass decided, and the sentence a person reads.</summary>
public sealed record LauncherUpdateResult(LauncherUpdateDecision Decision, string Message, IReadOnlyList<string> Steps)
{
    public static LauncherUpdateResult Of(LauncherUpdateDecision decision, string message)
        => new(decision, message, Array.Empty<string>());
}

/// <summary>
/// THE DIRECTOR'S OWNERSHIP OF THE LAUNCHER'S UPDATE - the mirror image of
/// <c>DirectorUpdateOwner</c>, which is the launcher owning the Director's update (issue #1033).
///
/// WHY IT HAS TO BE THE DIRECTOR. Whatever replaces a binary must outlive the process being replaced.
/// The launcher cannot honestly swap itself: the only thing that could confirm the new build came up
/// is the process that just exited. That argument is why the Director's update moved to the launcher,
/// and it is the same argument in the other direction - so the launcher's update belongs to the
/// Director, which is running beside it and is still there afterwards.
///
/// WHY THIS IS THE BOOTSTRAP, AND WHY IT IS URGENT. Nothing owned the launcher's update, so a launcher
/// that fell behind stayed behind. On 2026-09-06 this account's main machine was running launcher
/// 1.9.8, two days older than the code that lets a launcher be told anything at all, with a 2.0.4
/// build staged since 1 September that nothing ever installed. That machine could not be commanded and
/// could not be fixed remotely, because the thing that would receive the fix WAS the broken thing. A
/// Director update still reaches such a machine, so this rides in with one.
///
/// TWO THINGS HERE ARE EASY TO GET WRONG AND BOTH WOULD FAIL SILENTLY:
///
/// First, THE WITNESS IS INVERTED. The Director's update is proved by the new Director answering with
/// the new version. A launcher that starts and holds no command surface looks perfectly healthy from
/// outside and is exactly today's failure, so "a process started" proves nothing here. The proof is
/// <see cref="LauncherWitness"/>: a live registration declaring the new version AND a listener on the
/// signal that tells the launcher to restart the Director.
///
/// Second, THE SWAP MUST NEVER TAKE THE DIRECTOR WITH IT. On Windows the launcher is the Director's
/// parent process, so stopping it with a process-TREE kill would kill the very process performing the
/// swap. Nothing here ever kills a tree; it asks politely first, and insists on one process id at a
/// time. The Director keeps running across the whole swap and the new launcher re-adopts it - the
/// launcher never starts a Director that already holds its instance.
///
/// AND THE ROOT IS THE SHARED ONE. A Director redirects its data tree to its instance home, so a
/// layout resolved by <see cref="InstallLayout.Default"/> inside a Director points at
/// <c>instances/&lt;slug&gt;/launcher</c> and <c>instances/&lt;slug&gt;/state</c> - directories no
/// installer has ever written. Read that way, this would report "nothing staged" for ever, on every
/// machine, while wired and logged and apparently working. The shared root is passed in.
/// </summary>
public sealed class LauncherUpdateOwner
{
    private readonly string _sharedRoot;
    private readonly InstallLayout _layout;
    private readonly LauncherWitness _witness;
    private readonly LauncherSelfUpdate _apply;
    private readonly TimeSpan _witnessTimeout;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <param name="sharedRoot">
    /// The machine-wide cc-director root - <c>InstanceContext.SharedRoot</c> inside a Director. NOT the
    /// calling process's storage home; see the class comment.
    /// </param>
    /// <param name="directorStillHoldsItsInstance">
    /// Asked after the swap: is this Director still the live owner of its instance? It is the check
    /// that the launcher swap did not orphan or duplicate it. Required rather than defaulted, because a
    /// default that always answered yes would be a check that cannot fail.
    /// </param>
    public LauncherUpdateOwner(
        string sharedRoot,
        Func<bool> directorStillHoldsItsInstance,
        LauncherWitness? witness = null,
        LauncherSelfUpdate? apply = null,
        TimeSpan? witnessTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentNullException.ThrowIfNull(directorStillHoldsItsInstance);

        _sharedRoot = sharedRoot;
        _layout = new InstallLayout(sharedRoot);
        _witness = witness ?? new LauncherWitness(sharedRoot);
        _apply = apply ?? new LauncherSelfUpdate(_layout);
        // A cold launcher start unpacks a ~134 MB single-file binary before it registers anything, and
        // on the machine where somebody is watching for the first time that is slow exactly once.
        _witnessTimeout = witnessTimeout ?? TimeSpan.FromMinutes(3);
        DirectorStillHoldsItsInstance = directorStillHoldsItsInstance;
    }

    /// <summary>See the constructor.</summary>
    public Func<bool> DirectorStillHoldsItsInstance { get; }

    /// <summary>Every cc-launcher process on the machine (unfiltered - this class does the scoping).</summary>
    public Func<IReadOnlyList<LauncherProcess>> ListLauncherProcesses { get; init; } = InstalledLauncherProcesses.List;

    /// <summary>Ask the launcher serving a storage root to quit. True when the request was delivered.</summary>
    public Func<string, bool> RequestQuit { get; init; } =
        root => LifecycleSignal.Raise(LifecycleSignalNames.LauncherShutdown(root));

    /// <summary>
    /// Stop ONE process id, escalating from polite to insistent. NEVER a process tree - the rule lives
    /// in <see cref="SingleProcessStop"/>, which is the only place in the swap path that kills
    /// anything, so this file contains no kill to get wrong.
    /// </summary>
    public Func<int, bool> StopProcess { get; init; } = SingleProcessStop.Stop;

    /// <summary>Start the installed launcher in managed mode. Returns the process id, or 0 when it could not be started.</summary>
    public Func<InstallLayout, int> StartLauncher { get; init; } = DefaultStartLauncher;

    /// <summary>
    /// What version does a build on disk declare about itself? Production reads the file's own stamp;
    /// a test supplies one, because a fake build has no version resource to read.
    /// </summary>
    public Func<string, string?> ReadVersionOnDisk { get; init; } = InstalledStateReader.ReadVersionFromDisk;

    /// <summary>
    /// The machine-wide lock this swap takes. <see cref="BinarySwapLock.Name"/> in production, always;
    /// a test overrides it so it can hold a lock of its own without contending with everything else on
    /// the machine. See the parameter note on <see cref="BinarySwapLock.RunExclusivelyAsync"/>.
    /// </summary>
    public string SwapLockName { get; init; } = BinarySwapLock.Name;

    /// <summary>How long to wait for the stopped launcher's processes to go before insisting.</summary>
    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a process that was insisted upon is given to actually go before it is judged.</summary>
    public TimeSpan StopSettleTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Where a staged launcher build waits - one definition, shared with the launcher's own updater.</summary>
    public string StagedBuildPath => new LauncherUpdater(_layout).StagedExePath;

    /// <summary>
    /// One pass: look for a staged launcher build newer than the one installed, and install it. Never
    /// throws - this runs on a background loop, so every failure comes back as a decision and a log line.
    /// </summary>
    public async Task<LauncherUpdateResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (!await _oneAtATime.WaitAsync(TimeSpan.Zero, ct))
        {
            FileLog.Write("[LauncherUpdateOwner] a previous pass is still running; skipping this one.");
            return LauncherUpdateResult.Of(LauncherUpdateDecision.HeldBecauseUndecidable,
                "A previous launcher update pass is still running.");
        }

        try
        {
            return await RunOnceCoreAsync(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherUpdateOwner] RunOnceAsync FAILED: {ex}");
            return LauncherUpdateResult.Of(LauncherUpdateDecision.Failed, $"The launcher update pass failed: {ex.Message}");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<LauncherUpdateResult> RunOnceCoreAsync(CancellationToken ct)
    {
        var staged = FindStagedUpdate(out var why);
        if (staged is null)
        {
            FileLog.Write($"[LauncherUpdateOwner] nothing to do: {why}");
            return LauncherUpdateResult.Of(
                why.StartsWith("pinned", StringComparison.Ordinal)
                    ? LauncherUpdateDecision.SkippedPinnedBadVersion
                    : LauncherUpdateDecision.NothingStaged,
                why);
        }

        // WHICH process is the launcher, and is it ours? Both questions are asked before anything is
        // stopped. A machine where more than one installed launcher is running does not know whose
        // command surface it would be replacing, and a machine where none is running is not one this
        // loop reopens - the same rule the launcher applies to a closed Director, for the same reason:
        // starting an application somebody deliberately closed is not an update.
        var reading = _witness.Read();

        // WHERE A LAUNCHER CANNOT BE WITNESSED, NOTHING IS SWAPPED. On a platform whose command surface
        // cannot be observed at all, every swap this class performed would install the new build, fail
        // to certify it for the whole witness timeout, roll it back and PIN it - churning the machine
        // and permanently blacklisting a build that was probably fine. Refusing up front, and saying
        // which platform fact caused it, is the honest form of the same answer. See
        // LauncherWitnessReading.Witnessed for why the alternative - letting the witness pass on a live
        // registration alone - is the liveness-only proof this whole class exists to forbid.
        if (reading.CommandSurface == LauncherCommandSurface.NotObservable)
        {
            var notObservable =
                $"{staged.Version} is staged, but whether a launcher holds a command surface cannot be "
                + "observed on this platform, so a swap could never be certified. The Director's "
                + "ownership of the launcher's update is Windows-only until the launcher publishes its "
                + "own capability.";
            FileLog.Write($"[LauncherUpdateOwner] {notObservable}");
            return LauncherUpdateResult.Of(
                LauncherUpdateDecision.HeldBecauseTheCommandSurfaceCannotBeObserved, notObservable);
        }

        // ALREADY THE RIGHT BUILD - BUT ONLY IF IT CAN BE COMMANDED. This asked whether the running
        // process was ALIVE and reported the staged version, and then wrote that version into the
        // installed manifest. A launcher that is the right version and holds no command surface is
        // exactly the machine this whole change exists for, and that shortcut blessed it: the manifest
        // then agreed with the binary, so the pass reported nothing to do for ever and the machine
        // stayed uncommandable in silence. The version is recorded only when it was WITNESSED, and an
        // unwitnessed match falls through to the swap, which is the retry that fixes it.
        if (reading.Witnessed && VersionsMatch(staged.Version, reading.Version))
        {
            FileLog.Write($"[LauncherUpdateOwner] the running launcher already reports {reading.Version} and is "
                          + "commandable; nothing to install.");
            RecordInstalledVersion(staged.Version);
            return LauncherUpdateResult.Of(LauncherUpdateDecision.NothingStaged,
                $"The running launcher already reports {reading.Version} and can be commanded.");
        }

        if (reading.ProcessAlive && VersionsMatch(staged.Version, reading.Version))
            FileLog.Write($"[LauncherUpdateOwner] the running launcher already reports {reading.Version}, but it is "
                          + $"NOT witnessed ({reading.Detail}). Reinstalling it rather than recording a version that "
                          + "was never proved commandable.");

        List<LauncherProcess> ours;
        try
        {
            ours = InstalledLauncherProcesses.Ours(_layout.LauncherDir, ListLauncherProcesses());
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherUpdateOwner] the process list is unreadable: {ex.Message}");
            return LauncherUpdateResult.Of(LauncherUpdateDecision.HeldBecauseUndecidable,
                $"The process list could not be read, so which process the launcher is could not be established: {ex.Message}");
        }

        if (ours.Count == 0)
        {
            FileLog.Write($"[LauncherUpdateOwner] {staged.Version} is staged but no launcher is running from "
                          + $"{_layout.LauncherDir}. Leaving both alone: it is installed the next time a launcher starts, "
                          + "and nothing here reopens a launcher somebody closed.");
            return LauncherUpdateResult.Of(LauncherUpdateDecision.HeldBecauseLauncherNotRunning,
                $"{staged.Version} is staged, but no launcher is running from {_layout.LauncherDir}.");
        }

        if (ours.Count > 1)
        {
            var pids = string.Join(", ", ours.Select(p => p.Pid));
            FileLog.Write($"[LauncherUpdateOwner] REFUSING to install {staged.Version}: {ours.Count} installed launcher "
                          + $"processes are running ({pids}), so which one holds the command surface is unknown.");
            return LauncherUpdateResult.Of(LauncherUpdateDecision.HeldBecauseUndecidable,
                $"{ours.Count} installed launcher processes are running ({pids}); nothing was touched.");
        }

        FileLog.Write($"[LauncherUpdateOwner] installing launcher {staged.Version}: staged={staged.StagedBuild}, "
                      + $"target={staged.InstallTarget}, running={reading.Detail}");

        // THE SWAP IS EXCLUSIVE, MACHINE-WIDE. The launcher may at this very moment be installing the
        // DIRECTOR's staged update - which stops this process, mid-swap, with the launcher binary
        // already renamed aside. See BinarySwapLock for both directions of that race and for what the
        // lock does not cover.
        return await BinarySwapLock.RunExclusivelyAsync(
            () => SwapAsync(staged, ct),
            whenBusy: why => LauncherUpdateResult.Of(LauncherUpdateDecision.HeldBecauseAnotherSwapIsRunning,
                $"{staged.Version} is staged, but {why}"),
            who: "launcher-update",
            name: SwapLockName);
    }

    /// <summary>
    /// The swap itself, run while <see cref="BinarySwapLock"/> is held: stop, replace, start, witness,
    /// and then check that this Director survived it.
    /// </summary>
    private async Task<LauncherUpdateResult> SwapAsync(StagedLauncherUpdate staged, CancellationToken ct)
    {
        var result = await _apply.ApplyAsync(
            staged.InstallTarget,
            staged.StagedBuild,
            staged.Version,
            stopLauncher: StopInstalledLauncher,
            startLauncher: () => StartLauncher(_layout) > 0,
            // THE WITNESS, and the whole reason this is not the Director's update owner with the nouns
            // changed: the new build is proved by a launcher that is running AND can be commanded,
            // reporting the version just installed. A launcher that starts and opens nothing fails this.
            isHealthy: _ => Task.FromResult(VersionsMatch(staged.Version, _witness.WitnessedVersion())),
            healthTimeout: _witnessTimeout,
            ct: ct);

        var steps = new List<string>(result.Steps);

        // THE NO-ORPHAN CHECK. The swap stops a process that on Windows is the Director's parent, so the
        // one thing that must be true afterwards is that this Director is still the live owner of its
        // instance - not killed with the launcher, and not duplicated by the new one.
        bool stillOurs;
        try { stillOurs = DirectorStillHoldsItsInstance(); }
        catch (Exception ex)
        {
            stillOurs = false;
            FileLog.Write($"[LauncherUpdateOwner] the no-orphan check itself failed: {ex.Message}");
        }
        steps.Add(stillOurs
            ? "this Director still holds its instance after the swap"
            : "THIS DIRECTOR NO LONGER HOLDS ITS INSTANCE after the launcher swap");
        steps.Add($"after the swap: {_witness.Read().Detail}");

        foreach (var step in steps)
            FileLog.Write($"[LauncherUpdateOwner]   step: {step}");

        // THE NO-ORPHAN CHECK IS AN INVARIANT, NOT A NOTE. A failed check used to return Applied with a
        // longer message, so every caller that switched on the decision - which is what a caller does -
        // read "the launcher swap orphaned this Director" as a successful update, and the warning rode
        // along in a string nothing inspects. It gets its own answer, which cannot be mistaken for one.
        var decision = result.Outcome switch
        {
            SelfUpdateOutcome.Updated when !stillOurs => LauncherUpdateDecision.AppliedButThisDirectorLostItsInstance,
            SelfUpdateOutcome.Updated => LauncherUpdateDecision.Applied,
            SelfUpdateOutcome.RolledBack => LauncherUpdateDecision.RolledBack,
            _ => LauncherUpdateDecision.Failed,
        };

        var message = stillOurs
            ? result.Message
            : result.Message + " THE DIRECTOR NO LONGER HOLDS ITS INSTANCE - the launcher swap took it with it.";

        FileLog.Write($"[LauncherUpdateOwner] launcher {staged.Version}: {decision} - {message}");
        return new LauncherUpdateResult(decision, message, steps);
    }

    /// <summary>
    /// The staged launcher build waiting for THIS install, or null with the reason in
    /// <paramref name="why"/>.
    /// </summary>
    public StagedLauncherUpdate? FindStagedUpdate(out string why)
    {
        var stagedPath = StagedBuildPath;
        if (!File.Exists(stagedPath))
        {
            why = $"no launcher build is staged at {stagedPath}";
            return null;
        }

        var stagedVersion = ReadVersionOnDisk(stagedPath);
        if (string.IsNullOrWhiteSpace(stagedVersion))
        {
            // A build that will not say what it is cannot be compared with what is installed, and
            // installing it anyway would mean not knowing afterwards whether it was the right one.
            why = $"the build staged at {stagedPath} does not declare a version, so it cannot be installed";
            FileLog.Write($"[LauncherUpdateOwner] {why}");
            return null;
        }

        var installed = new InstalledStateReader(_layout).Read(ComponentRegistry.Launcher);
        if (!installed.Present)
        {
            // Refresh-only, exactly like the launcher's own updater: this installs a NEWER launcher over
            // an existing one. Placing a launcher on a machine that has none is an install, and installs
            // are the installer's job.
            why = $"no launcher is installed at {_layout.PathFor(ComponentRegistry.Launcher)}, so there is nothing to update";
            return null;
        }

        if (!VersionUtil.IsNewer(stagedVersion, installed.Version))
        {
            why = $"the staged launcher {stagedVersion} is not newer than the installed {installed.Version ?? "unknown"}";
            return null;
        }

        if (PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, stagedVersion))
        {
            why = $"pinned: launcher {stagedVersion} already failed to come up and is pinned away from";
            FileLog.Write($"[LauncherUpdateOwner] {why}");
            return null;
        }

        why = "";
        return new StagedLauncherUpdate(stagedVersion, stagedPath, _layout.PathFor(ComponentRegistry.Launcher));
    }

    /// <summary>
    /// Stop the installed launcher: ask it to quit, wait, and insist on whatever is left - ONE PROCESS
    /// AT A TIME AND NEVER A TREE. True when no installed launcher process remains.
    ///
    /// The tree is the trap. The uninstaller's stop kills entire process trees, which is defensible
    /// when the whole install is going away; here the Director is the launcher's child on Windows, so a
    /// tree kill would end the process running this code, mid-swap, with the launcher binary half
    /// replaced.
    ///
    /// A launcher too old to listen for the quit signal is the NORMAL case for this class - the
    /// launchers that most need replacing are the ones that cannot be asked - so the polite request
    /// failing is logged as the expected fact it is, not as an error.
    /// </summary>
    public bool StopInstalledLauncher()
    {
        var asked = RequestQuit(_sharedRoot);
        FileLog.Write(asked
            ? "[LauncherUpdateOwner] the launcher was asked to quit"
            : "[LauncherUpdateOwner] no launcher is listening for the quit request - expected of a build that "
              + "predates the lifecycle signals, which is exactly the build this replaces");

        if (asked) WaitFor(() => OursNow().Count == 0, GracefulStopTimeout);

        foreach (var p in OursNow())
        {
            if (p.Pid == Environment.ProcessId)
            {
                // Cannot happen with a correctly scoped list, and is asserted anyway: this process is a
                // Director, and a Director that stopped itself here would leave the swap unfinished.
                FileLog.Write("[LauncherUpdateOwner] REFUSING to stop this very process.");
                continue;
            }

            var exited = StopProcess(p.Pid);
            FileLog.Write(exited
                ? $"[LauncherUpdateOwner] stopped installed launcher process {p.Pid}"
                : $"[LauncherUpdateOwner] could NOT stop installed launcher process {p.Pid}");
        }

        WaitFor(() => OursNow().Count == 0, StopSettleTimeout);
        var remaining = OursNow();
        if (remaining.Count > 0)
            FileLog.Write("[LauncherUpdateOwner] installed launcher process(es) STILL RUNNING: "
                          + string.Join(", ", remaining.Select(p => p.Pid)));
        return remaining.Count == 0;
    }

    private List<LauncherProcess> OursNow()
    {
        try
        {
            return InstalledLauncherProcesses.Ours(_layout.LauncherDir, ListLauncherProcesses());
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherUpdateOwner] listing launcher processes failed: {ex.Message}");
            // Unreadable is NOT empty. Answering "none left" here would report a stop nobody observed,
            // so an unreadable list yields a process id nothing can stop and the caller waits its
            // timeout out instead of believing a clean result.
            return [new LauncherProcess(0, "the process list is unreadable")];
        }
    }

    /// <summary>Record the version now installed, so the manifest and the binary do not drift apart.</summary>
    private void RecordInstalledVersion(string version)
    {
        try
        {
            var manifest = InstalledManifest.Load(_layout);
            if (string.Equals(manifest.Get(ComponentRegistry.Launcher.Id), version, StringComparison.Ordinal)) return;
            manifest.Set(ComponentRegistry.Launcher.Id, version);
            manifest.Save(_layout);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherUpdateOwner] recording launcher {version} failed: {ex.Message}");
        }
    }

    /// <summary>Do these two versions describe the same build, ignoring any build metadata?</summary>
    private static bool VersionsMatch(string expected, string? reported)
    {
        var left = VersionUtil.TryParse(expected);
        var right = VersionUtil.TryParse(reported);
        return left is not null && right is not null && left == right;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(250);
        }
        return condition();
    }

    // ---- production implementations ----

    /// <summary>
    /// How the installed launcher is started: in managed mode, and NAMING the storage root it is to
    /// serve. Separated from the start itself so the environment it is given can be asserted - the
    /// value here is the whole difference between a swap that comes up and one that silently does not.
    ///
    /// <c>CC_DIRECTOR_ROOT</c> IS SET, NOT REMOVED, AND THAT IS THE POINT. A Director sets that
    /// variable to its own INSTANCE HOME, and a child inherits it - so a launcher started from inside a
    /// Director without any handling takes the instance home for the machine root and goes on to
    /// supervise, register and signal in a tree no installer has ever written. This first fixed that by
    /// REMOVING the variable, which is the right intent and the wrong mechanism: removing it does not
    /// name the correct root, it merely stops naming the wrong one and leaves the child to fall back on
    /// the process default. That default is the same thing as this install's root only when the install
    /// happens to sit at the default location.
    ///
    /// Where it does not, the failure is silent and expensive, and it was OBSERVED rather than
    /// reasoned about: on 2026-09-06 the end-to-end rig (scripts/launcher-swap-proof.ps1) swapped a
    /// launcher on an isolated root, started the new build with the variable removed, and the new
    /// process resolved to the MACHINE'S root instead - where it found the real launcher already
    /// running and logged "CC Launcher already running in this session; exiting second instance". It
    /// therefore never registered where the witness was looking, the witness correctly refused, the
    /// swap rolled back, and a perfectly good build was PINNED. Four starts across two runs, all four
    /// identical. An install that is not at the default path would have been left permanently refusing
    /// its own launcher update, and the log would have said the build was bad.
    ///
    /// So the root is stated positively. It also still does the original job: an explicit value cannot
    /// be an inherited instance home.
    /// </summary>
    public static ProcessStartInfo BuildLauncherStartInfo(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var psi = new ProcessStartInfo
        {
            FileName = layout.PathFor(ComponentRegistry.Launcher),
            WorkingDirectory = layout.LauncherDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(LauncherTrayInstaller.InstalledArguments);
        psi.Environment["CC_DIRECTOR_ROOT"] = layout.LocalRoot;
        return psi;
    }

    /// <summary>Start the installed launcher. See <see cref="BuildLauncherStartInfo"/> for the environment.</summary>
    private static int DefaultStartLauncher(InstallLayout layout)
    {
        try
        {
            var psi = BuildLauncherStartInfo(layout);
            using var started = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for {psi.FileName}");
            FileLog.Write($"[LauncherUpdateOwner] started the launcher: pid={started.Id}, exe={psi.FileName} "
                          + $"{LauncherTrayInstaller.InstalledArguments}, CC_DIRECTOR_ROOT={layout.LocalRoot}");
            return started.Id;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherUpdateOwner] starting the launcher FAILED: {ex.Message}");
            return 0;
        }
    }
}
