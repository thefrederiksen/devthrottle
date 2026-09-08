using System.Diagnostics;
using CcDirector.Core.Instances;
using CcDirector.Core.Lifecycle;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// RESTART ONLY IF THE DIRECTOR IS EMPTY - the mechanical guarantee behind "never force".
///
/// A drain empties a Director one session at a time, and a restart that arrives in the middle of one
/// takes every session that is left with it. The rule against that used to be a sentence in a written
/// instruction; these tests are the rule being enforced by the launcher instead.
///
/// THE REFUSAL IS THE FEATURE, so it is tested first and hardest. A suite that only proved the happy
/// path would prove nothing here: an implementation that ignored the flag entirely and restarted every
/// time would pass it.
///
/// HOW THE RIG WORKS, because the fidelity is the point. Nothing is mocked. A real helper process stands
/// in for the Director, a real instance registration names it (so the real
/// <see cref="DirectorInstanceLocator"/> resolves it exactly as it resolves a live Director), and a real
/// <see cref="DirectorCrashJournal"/> writes the live session roster the launcher reads to get its count.
/// The helper LISTENS FOR THE SAME SHUTDOWN SIGNAL a Director listens for and exits when it is raised, so
/// "the Director was stopped" is an observed process exit rather than an assumption.
///
/// WHAT THE PERMITTED CASE OBSERVES, AND WHAT IT DOES NOT. When the guard permits, the launcher stops the
/// Director and then starts the INSTALLED one - and this rig deliberately installs no Director under its
/// temporary root, so that last step throws <see cref="FileNotFoundException"/> naming the exact path it
/// went to. That throw IS the observation that the restart proceeded; what it does not prove is that a
/// real Director comes up afterwards, which is the shipped, unchanged half of
/// <see cref="DirectorSupervisor.RestartAsync(CancellationToken)"/> and is not what this change touches.
/// </summary>
public sealed class RestartOnlyIfEmptyTests
{
    // -------------------------------------------------------------------------
    // ASSERTION ONE: sessions live -> it refuses, AND the refusal names the count.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RestartAsync_OnlyIfEmpty_RefusesAndNamesTheLiveSessionCount()
    {
        if (!OperatingSystem.IsWindows()) return; // the rig's stand-in Director is a Windows helper process

        using var rig = Rig.Start(liveSessions: 3);

        var outcome = await rig.Supervisor.RestartAsync(onlyIfEmpty: true);

        Assert.Equal(DirectorRestartVerdict.Refused, outcome.Verdict);
        Assert.Equal(3, outcome.Sessions);

        // NAMING THE COUNT IS THE REQUIREMENT. "Refused, 3 sessions still live" is actionable - the drain
        // is not finished - and a bare "refused" is not. The number and the noun are asserted TOGETHER on
        // purpose: a bare Contains("3") would also pass on a refusal that named no count at all, because
        // the Director's identifier is a globally unique identifier and usually contains a 3.
        Assert.Contains("3 live sessions", outcome.Reason);

        // AND NOTHING WAS DONE. A refusal that had already stopped the Director would be the exact damage
        // this guard exists to prevent, wearing an apology.
        rig.Helper.Refresh();
        Assert.False(rig.Helper.HasExited,
            "the restart was refused and yet the Director was stopped anyway - the guard ran too late.");
    }

    // -------------------------------------------------------------------------
    // ASSERTION TWO: no sessions -> it restarts.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RestartAsync_OnlyIfEmpty_StopsThenStartsTheDirector_WhenItHoldsNoSessions()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var rig = Rig.Start(liveSessions: 0);

        // The guard's own verdict first: an empty Director is not refused, and it says the count it
        // reached that on rather than leaving the caller to read the machine a second time.
        Assert.Null(rig.Supervisor.RefuseRestartUnlessEmpty(out var counted, out _));
        Assert.Equal(0, counted);

        // Then the whole restart. It stops the Director it found and goes on to start the installed one,
        // which this rig does not install - so the launch throws, naming the path it went to.
        var launch = await Assert.ThrowsAsync<FileNotFoundException>(
            () => rig.Supervisor.RestartAsync(onlyIfEmpty: true));
        Assert.Contains(rig.Supervisor.DirectorExePath, launch.Message, StringComparison.OrdinalIgnoreCase);

        // The stop is observed, not assumed: the stand-in Director received the shutdown signal and exited.
        Assert.True(WaitForExit(rig.Helper),
            "the restart was permitted and yet the Director was never stopped.");
    }

    // -------------------------------------------------------------------------
    // The A/B that shows the flag is what makes the difference: the SAME busy Director,
    // restarted without it, is restarted.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RestartAsync_WithoutOnlyIfEmpty_StillRestartsABusyDirector()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var rig = Rig.Start(liveSessions: 3);

        var launch = await Assert.ThrowsAsync<FileNotFoundException>(
            () => rig.Supervisor.RestartAsync(onlyIfEmpty: false));
        Assert.Contains(rig.Supervisor.DirectorExePath, launch.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(WaitForExit(rig.Helper),
            "an unconditional restart must still restart a busy Director - the tray menu and the "
            + "staged-update signal both depend on it.");
    }

    // -------------------------------------------------------------------------
    // The fail-open case: a count that could not be read is a REFUSAL, never an empty Director.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RestartAsync_OnlyIfEmpty_RefusesWhenTheLiveSessionCountCannotBeRead()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A running Director with no readable roster - which is what a Director that has not yet opened
        // its crash journal looks like from outside. It is indistinguishable from an idle one, and that is
        // exactly why it must not be treated as idle.
        using var rig = Rig.Start(liveSessions: null);

        var outcome = await rig.Supervisor.RestartAsync(onlyIfEmpty: true);

        Assert.Equal(DirectorRestartVerdict.Refused, outcome.Verdict);
        Assert.Null(outcome.Sessions);
        Assert.Contains("unknown", outcome.Reason, StringComparison.OrdinalIgnoreCase);

        rig.Helper.Refresh();
        Assert.False(rig.Helper.HasExited,
            "an unreadable session count was treated as an empty Director and the restart went ahead.");
    }

    /// <summary>
    /// The OTHER unknown: two live processes claim the instance, so which one is the Director cannot be
    /// decided and neither can its session count. Refused, for the same reason an unreadable roster is.
    /// </summary>
    [Fact]
    public async Task RestartAsync_OnlyIfEmpty_RefusesWhenWhichProcessIsTheDirectorIsUndecidable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "cc-restart-ambiguous-" + Guid.NewGuid().ToString("N"));
        var instanceHome = Path.Combine(root, "instances", "default");
        var registrations = Path.Combine(instanceHome, "config", "director", "instances");
        Directory.CreateDirectory(registrations);

        using var first = StartIdleHelper();
        using var second = StartIdleHelper();
        try
        {
            WriteRegistration(registrations, Guid.NewGuid().ToString(), first.Id);
            WriteRegistration(registrations, Guid.NewGuid().ToString(), second.Id);

            var locator = new DirectorInstanceLocator(instanceHome);
            Assert.Equal(DirectorResolution.Ambiguous, locator.Resolve().Outcome);

            var supervisor = new DirectorSupervisor(new InstallLayout(root), locator);
            var outcome = await supervisor.RestartAsync(onlyIfEmpty: true);

            Assert.Equal(DirectorRestartVerdict.Refused, outcome.Verdict);
            Assert.Null(outcome.Sessions);
            Assert.Contains("undecidable", outcome.Reason, StringComparison.OrdinalIgnoreCase);

            first.Refresh();
            second.Refresh();
            Assert.False(first.HasExited || second.HasExited,
                "a machine nobody can make sense of was restarted anyway.");
        }
        finally
        {
            try { if (!first.HasExited) first.Kill(entireProcessTree: true); } catch { }
            try { if (!second.HasExited) second.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A RESTART THAT STOPS AND STARTS NOTHING IS NOT A REFUSAL. The distinction was missed in the first
    /// draft of this feature and found by review: a refusal promises the machine is exactly as the caller
    /// left it, and this outcome promises the opposite - the stop ran and nothing came back. Reporting it
    /// as a refusal would tell somebody their Director is untouched at the moment it has gone.
    /// </summary>
    [Fact]
    public async Task ARestartThatStartsNothing_IsReportedAsNotStarted_AndOnTheWireAsAFault()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "cc-restart-nostart-" + Guid.NewGuid().ToString("N"));
        var instanceHome = Path.Combine(root, "instances", "default");
        var registrations = Path.Combine(instanceHome, "config", "director", "instances");
        Directory.CreateDirectory(registrations);

        // An installed Director must EXIST for the launch to get as far as being skipped - Start checks
        // the file before it checks the machine. It is never executed here: two live processes already
        // claim the instance, so the launch is declined before anything is started.
        var layout = new InstallLayout(root);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.PathFor(ComponentRegistry.Director))!);
        File.WriteAllText(layout.PathFor(ComponentRegistry.Director), "not a real Director");

        using var first = StartIdleHelper();
        using var second = StartIdleHelper();
        try
        {
            WriteRegistration(registrations, Guid.NewGuid().ToString(), first.Id);
            WriteRegistration(registrations, Guid.NewGuid().ToString(), second.Id);

            var supervisor = new DirectorSupervisor(layout, new DirectorInstanceLocator(instanceHome));
            var outcome = await supervisor.RestartAsync(onlyIfEmpty: false);

            Assert.Equal(DirectorRestartVerdict.NotStarted, outcome.Verdict);
            Assert.Null(outcome.StartedPid);
            Assert.Contains("no new Director was started", outcome.Reason);

            // And on the wire it is a fault, not the 409 a refusal becomes.
            await using var launcher = DispatchOnly(supervisor);
            var result = await launcher.DispatchAsync(new LauncherCommand { Verb = "director/restart" });
            Assert.Equal(LauncherCommandStatus.Error, result.Status);
        }
        finally
        {
            try { if (!first.HasExited) first.Kill(entireProcessTree: true); } catch { }
            try { if (!second.HasExited) second.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A Director that is not running is EMPTY, and that is a real answer rather than a missing one -
    /// nothing is holding a session because nothing is holding anything. The restart then does what a
    /// restart of a stopped Director has always done, which is go and start one.
    /// </summary>
    [Fact]
    public async Task RestartAsync_OnlyIfEmpty_StartsTheDirector_WhenNoneIsRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-restart-stopped-" + Guid.NewGuid().ToString("N"));
        var instanceHome = Path.Combine(root, "instances", "default");
        Directory.CreateDirectory(instanceHome);
        try
        {
            var supervisor = new DirectorSupervisor(new InstallLayout(root),
                new DirectorInstanceLocator(instanceHome));

            Assert.Null(supervisor.RefuseRestartUnlessEmpty(out var counted, out _));
            Assert.Equal(0, counted);

            var launch = await Assert.ThrowsAsync<FileNotFoundException>(
                () => supervisor.RestartAsync(onlyIfEmpty: true));
            Assert.Contains(supervisor.DirectorExePath, launch.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A RESOLVED CONFLICT IS REFUSED, not counted. The locator has a state the first version of this
    /// guard did not model: several live processes claim one instance home AND the tie-break can name
    /// the installed one, so there IS a Director and its roster reads perfectly. An independent review
    /// found the guard permitting there - it asked whether a Director had been resolved and never
    /// whether resolving it had been a conflict.
    ///
    /// The rig gives the locator an installed image to break the tie with, which is what separates this
    /// from the undecidable case above: there the answer is "none of them", here it is "this one, and
    /// something else is also claiming the home".
    /// </summary>
    [Fact]
    public async Task RestartAsync_OnlyIfEmpty_RefusesWhenTheMachineHoldsAResolvedConflict()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "cc-restart-conflict-" + Guid.NewGuid().ToString("N"));
        var instanceHome = Path.Combine(root, "instances", "default");
        var registrations = Path.Combine(instanceHome, "config", "director", "instances");
        var journalDir = Path.Combine(instanceHome, "config", "director", "crash-journal");
        Directory.CreateDirectory(registrations);

        // The installed Director, as a real file, so one claimant can be certified against it as the
        // installed image and the tie-break has something to decide with.
        var layout = new InstallLayout(root);
        var installed = layout.PathFor(ComponentRegistry.Director);
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), installed);

        using var theInstalledOne = StartIdleHelper(installed);
        using var theOtherClaimant = StartIdleHelper();
        try
        {
            var installedId = Guid.NewGuid().ToString();
            WriteRegistration(registrations, installedId, theInstalledOne.Id);
            WriteRegistration(registrations, Guid.NewGuid().ToString(), theOtherClaimant.Id);

            var locator = new DirectorInstanceLocator(instanceHome, null, installed);
            var lookup = locator.Resolve();

            // The rig is only the rig if the locator really produced the resolved-conflict state.
            Assert.Equal(DirectorResolution.Running, lookup.Outcome);
            Assert.NotNull(lookup.Director);
            Assert.NotNull(lookup.Conflict);

            // And an empty roster for the chosen Director, so nothing but the conflict can be the reason.
            new DirectorCrashJournal(lookup.Director!.DirectorId, theInstalledOne.Id, Environment.MachineName,
                Environment.UserName, DateTimeOffset.UtcNow, journalDir).Update(Array.Empty<DirectorCrashJournalSession>());
            Assert.Equal(0, locator.ReadSessionCount(lookup.Director!));

            var supervisor = new DirectorSupervisor(layout, locator);
            var outcome = await supervisor.RestartAsync(onlyIfEmpty: true);

            Assert.Equal(DirectorRestartVerdict.Refused, outcome.Verdict);
            Assert.Contains("more than one live process claims", outcome.Reason);

            theInstalledOne.Refresh();
            theOtherClaimant.Refresh();
            Assert.False(theInstalledOne.HasExited || theOtherClaimant.HasExited,
                "a machine with two claimants on one instance home was acted on anyway.");
        }
        finally
        {
            try { if (!theInstalledOne.HasExited) theInstalledOne.Kill(entireProcessTree: true); } catch { }
            try { if (!theOtherClaimant.HasExited) theOtherClaimant.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// ISSUE #2730, ASSERTED ON THE FIRST TREE THAT CARRIES BOTH HALVES: a registration the locator
    /// cannot read must make a guarded restart REFUSE, not proceed.
    ///
    /// WHAT THIS TEST USED TO SAY, AND WHY IT IS REWRITTEN RATHER THAN DELETED. On the branch that built
    /// the guard, the locator SKIPPED a registration it could not parse and answered NotRunning, so the
    /// guard permitted the restart as a start - and this test asserted only that the stop then asked
    /// nobody, which was the most the guard could promise on that locator. That was the fail-open the
    /// issue names: "I could not read what is there" arriving as "nothing is there", on the one question
    /// where the same answer means PERMIT. The locator fix gives that state its own name, Unknown, and the
    /// guard's exhaustive-by-name rule then refuses it. The two halves were built on separate branches
    /// (the guard on #2721, the locator on the fix for #2730) and this is the first tree with both, so this
    /// is the first place the property can be asserted rather than described.
    ///
    /// WHAT THIS TEST OBSERVES: a live process, registered under a file that will not parse, listening for
    /// its own shutdown signal. The guarded restart is REFUSED - it does not go on to a start - the refusal
    /// says why in the locator's terms, and the process is still alive afterwards, which is the evidence
    /// that nothing was asked to stop.
    /// </summary>
    [Fact]
    public async Task RestartAsync_OnlyIfEmpty_RefusesAnUnreadableRegistration_AndAsksNobodyToStop()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "cc-restart-2730-" + Guid.NewGuid().ToString("N"));
        var instanceHome = Path.Combine(root, "instances", "default");
        var registrations = Path.Combine(instanceHome, "config", "director", "instances");
        Directory.CreateDirectory(registrations);

        var directorId = Guid.NewGuid().ToString();
        using var director = Rig.StartShutdownListeningHelper(directorId);
        try
        {
            // A registration that will not parse. Before the locator fix this read as NotRunning; now it
            // reads as Unknown, which is the fact the whole issue turned on.
            File.WriteAllText(Path.Combine(registrations, directorId + ".json"), "{ this will not parse");

            var locator = new DirectorInstanceLocator(instanceHome);
            Assert.Equal(DirectorResolution.Unknown, locator.Resolve().Outcome);

            var supervisor = new DirectorSupervisor(new InstallLayout(root), locator);

            // THE ASSERTION: refused, not started. A permitted restart on this rig would throw
            // FileNotFoundException on the way to starting the uninstalled Director; a refusal returns.
            var outcome = await supervisor.RestartAsync(onlyIfEmpty: true);

            Assert.Equal(DirectorRestartVerdict.Refused, outcome.Verdict);
            Assert.Null(outcome.Sessions);
            Assert.Contains("undecidable", outcome.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Unknown", outcome.Reason);
            Assert.Contains("unknown count is never read as empty", outcome.Reason);

            // And nothing was asked to stop: the stand-in exits the moment its shutdown signal is raised,
            // so its being alive is the evidence that no signal was sent.
            director.Refresh();
            Assert.False(director.HasExited,
                "a guarded restart read an unreadable registration as an empty Director and stopped a live one anyway.");
        }
        finally
        {
            try { if (!director.HasExited) director.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// THE DEFECT REPRODUCED: the machine CHANGES between the guard's reading and the stop's, and the
    /// stop must still act on what the guard decided.
    ///
    /// This is the independent reviewer's counterexample, made deterministic. The first reading finds
    /// nothing running - which is what an unreadable registration looks like - so the guard permits and
    /// records a count of zero. The second reading, taken a moment later, finds a live Director holding
    /// three sessions. The old code took that second reading at stop time and would have stopped it: a
    /// restart that was allowed only because the machine looked empty, ending in three sessions being
    /// taken.
    ///
    /// The two readings are arranged through the supervisor's own reading seam, because outside a test
    /// they differ only when a real file finishes being written mid-call, which cannot be timed from the
    /// outside. Everything else is real: a live process listening for its own shutdown signal, and its
    /// exit or survival as the observation.
    /// </summary>
    [Fact]
    public async Task RestartAsync_OnlyIfEmpty_StopsWhatTheGuardSaw_EvenIfTheMachineChangesUnderneath()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var rig = Rig.Start(liveSessions: 3);

        // Reading one: nothing running. Reading two onwards: the truth - a live Director with three
        // sessions. A guard that decides on the first and a stop that acts on the second is the defect.
        var readings = 0;
        var theTruth = rig.Supervisor.Locator.Resolve();
        Assert.Equal(DirectorResolution.Running, theTruth.Outcome);

        rig.Supervisor.ReadTheMachine = () =>
            Interlocked.Increment(ref readings) == 1
                ? new DirectorLookup(DirectorResolution.NotRunning, null, Array.Empty<string>())
                : theTruth;

        // The guard permits on reading one, so the restart proceeds to start the installed Director,
        // which this rig does not install.
        await Assert.ThrowsAsync<FileNotFoundException>(() => rig.Supervisor.RestartAsync(onlyIfEmpty: true));

        Assert.True(readings >= 1, "the supervisor never read the machine at all");

        rig.Helper.Refresh();
        Assert.False(rig.Helper.HasExited,
            "the guard permitted a restart because the machine looked empty, and then the stop took a "
            + "SECOND reading and shut down a Director holding three sessions. The reading that decides "
            + "must be the reading that acts.");
    }

    // -------------------------------------------------------------------------
    // The command boundary: a "director/restart" arriving on the launcher's stream
    // -------------------------------------------------------------------------

    /// <summary>
    /// The seam between the command and the guard, which nothing else crosses. An adversarial review
    /// pointed out that a launcher passing a hard-coded false to the supervisor would pass every other
    /// test in this feature: the supervisor tests call the supervisor directly, and the Gateway tests
    /// answer with a stub launcher. This drives a real command through the real dispatch into a real
    /// supervisor, and the refusal it produces is the one the Gateway would receive on the wire.
    /// </summary>
    [Fact]
    public async Task ARestartCommandCarryingTheFlag_IsRefusedOnTheWire_NamingTheLiveSessionCount()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var rig = Rig.Start(liveSessions: 3);
        await using var launcher = DispatchOnly(rig.Supervisor);

        var result = await launcher.DispatchAsync(new LauncherCommand
        {
            Verb = "director/restart",
            OnlyIfEmpty = true,
        });

        Assert.Equal(LauncherCommandStatus.Refused, result.Status);
        Assert.Contains("3 live sessions", result.Error);

        rig.Helper.Refresh();
        Assert.False(rig.Helper.HasExited, "the command was refused and the Director was stopped anyway.");
    }

    /// <summary>
    /// The flag is refused on any verb that cannot honour it, at the boundary, rather than ignored. A verb
    /// that quietly drops it would answer a request to spare live work with a success that spared nothing.
    /// </summary>
    [Theory]
    [InlineData("director/stop")]
    [InlineData("director/start")]
    [InlineData("apps")]
    public async Task TheFlagOnAVerbThatCannotHonourIt_IsRefused_AndNothingIsDone(string verb)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var rig = Rig.Start(liveSessions: 3);
        await using var launcher = DispatchOnly(rig.Supervisor);

        var result = await launcher.DispatchAsync(new LauncherCommand { Verb = verb, OnlyIfEmpty = true });

        Assert.Equal(LauncherCommandStatus.BadRequest, result.Status);
        Assert.Contains("onlyIfEmpty", result.Error);

        rig.Helper.Refresh();
        Assert.False(rig.Helper.HasExited, $"'{verb}' carrying onlyIfEmpty acted on the Director anyway.");
    }

    /// <summary>
    /// A launcher stream client wired to a real supervisor and to nothing else. No Gateway is configured,
    /// so it dials nowhere and never starts a connection; <c>DispatchAsync</c> is the command path itself,
    /// which is the only part under test here.
    /// </summary>
    private static LauncherStreamClient DispatchOnly(DirectorSupervisor supervisor) =>
        new(new CcDirector.Core.Configuration.GatewayConfig(), version: "test", supervisor, new LaunchService());

    // -------------------------------------------------------------------------
    // The rig
    // -------------------------------------------------------------------------

    /// <summary>
    /// The instance registration a Director writes when it starts - the file the locator reads to learn
    /// which process it is supervising.
    /// </summary>
    private static void WriteRegistration(string registrationsDirectory, string directorId, int pid) =>
        File.WriteAllText(Path.Combine(registrationsDirectory, directorId + ".json"),
            $$"""
            {
              "DirectorId": "{{directorId}}",
              "Pid": {{pid}},
              "StartedAt": "{{DateTime.UtcNow:o}}",
              "ControlEndpoint": "",
              "Version": "9.9.9"
            }
            """);

    /// <summary>A harmless long-running process to stand in for a claimant that is never asked anything.</summary>
    private static Process StartIdleHelper() =>
        StartIdleHelper(Path.Combine(Environment.SystemDirectory, "cmd.exe"));

    /// <summary>
    /// The same stand-in, running a NAMED image - a copy of the command interpreter placed where the
    /// installed Director would be. It is what lets the locator certify one claimant as the installed
    /// image and break a tie, which is the only way to reach the resolved-conflict state from outside.
    /// </summary>
    private static Process StartIdleHelper(string exePath)
    {
        var psi = new ProcessStartInfo(exePath, "/c ping -n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start a helper process");
    }

    /// <summary>
    /// A temporary storage root holding one stand-in Director: a real live process, a real instance
    /// registration naming it, and (unless the count is meant to be unreadable) a real crash-journal
    /// roster carrying the live sessions.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly string _root;

        private Rig(string root, Process helper, DirectorSupervisor supervisor)
        {
            _root = root;
            Helper = helper;
            Supervisor = supervisor;
        }

        /// <summary>The stand-in Director process.</summary>
        public Process Helper { get; }

        public DirectorSupervisor Supervisor { get; }

        /// <param name="liveSessions">How many sessions the stand-in Director is holding, or null to write
        /// no roster at all - the "count cannot be read" case.</param>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static Rig Start(int? liveSessions)
        {
            var root = Path.Combine(Path.GetTempPath(), "cc-restart-empty-" + Guid.NewGuid().ToString("N"));
            var instanceHome = Path.Combine(root, "instances", "default");
            var registrations = Path.Combine(instanceHome, "config", "director", "instances");
            var journalDir = Path.Combine(instanceHome, "config", "director", "crash-journal");
            Directory.CreateDirectory(registrations);

            var directorId = Guid.NewGuid().ToString();
            var helper = StartShutdownListeningHelper(directorId);
            try
            {
                // The registration must be written AFTER the process starts: the locator rejects a
                // registration whose process started later than the file, which is how it throws out a
                // recycled process id. Writing it in the wrong order would make the whole rig resolve to
                // NotRunning and every test here would pass for the wrong reason.
                WriteRegistration(registrations, directorId, helper.Id);

                if (liveSessions is { } count)
                {
                    var journal = new DirectorCrashJournal(directorId, helper.Id, Environment.MachineName,
                        Environment.UserName, DateTimeOffset.UtcNow, journalDir);
                    journal.Update(Enumerable.Range(1, count).Select(i => new DirectorCrashJournalSession
                    {
                        SessionId = $"session-{i}",
                        Name = $"seat {i}",
                        RepoPath = @"D:\repo",
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                    }));
                }

                var layout = new InstallLayout(root);

                // No installed image to compare the claimant against, which is deliberate: a stand-in
                // cannot be running the installed Director's executable, and passing one would make the
                // locator answer NotSupervised and the rig would resolve to nothing. A lone claimant with
                // nothing to compare it to resolves as Running and UNCERTIFIED, which is the state the
                // launcher's own kill gate is written for and is exactly what a development build looks
                // like on a real machine.
                var locator = new DirectorInstanceLocator(instanceHome);

                // The rig is only a rig if the real locator agrees it is looking at a running Director.
                var lookup = locator.Resolve();
                Assert.Equal(DirectorResolution.Running, lookup.Outcome);
                Assert.Equal(liveSessions, locator.ReadSessionCount(lookup.Director!));

                return new Rig(root, helper, new DirectorSupervisor(layout, locator));
            }
            catch
            {
                Kill(helper);
                throw;
            }
        }

        public void Dispose()
        {
            Kill(Helper);
            try { Directory.Delete(_root, recursive: true); } catch { /* a temp directory */ }
        }

        private static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            p.Dispose();
        }

        /// <summary>
        /// A stand-in Director: a process that listens for THE SAME named shutdown signal a Director
        /// listens for and exits when it is raised. That is what makes "it was stopped" an observation -
        /// a helper that only slept would look identical whether the launcher asked it to stop or not.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        internal static Process StartShutdownListeningHelper(string directorId)
        {
            var signal = LifecycleSignalNames.DirectorShutdown(directorId);
            var script = "$e = New-Object System.Threading.EventWaitHandle "
                       + $"($false, 'ManualReset', 'Local\\{signal}'); "
                       + "[void]$e.WaitOne(120000)";

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };

            var helper = Process.Start(psi)
                         ?? throw new InvalidOperationException("could not start the stand-in Director");

            // Wait until it is actually LISTENING. Raising a signal nobody holds yet is not delivered, and
            // the launcher would then wait out its whole graceful-shutdown timeout - a test that takes
            // twenty seconds and proves the wrong thing.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\" + signal, out var opened))
                {
                    opened.Dispose();
                    return helper;
                }
                Thread.Sleep(50);
            }

            Kill(helper);
            throw new InvalidOperationException(
                $"the stand-in Director never began listening for {signal} within 30s");
        }
    }

    /// <summary>Whether a process has left the process table within a few seconds.</summary>
    private static bool WaitForExit(Process p) => p.WaitForExit(15_000);
}
