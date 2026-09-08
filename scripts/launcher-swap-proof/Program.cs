using CcDirector.Setup.Engine;

namespace CcDirector.Proof.LauncherSwap;

/// <summary>
/// THE END-TO-END PROOF of the Director installing the launcher's staged update (issue #2719,
/// Phase 0), driven against an ISOLATED storage root with REAL launcher binaries and a REAL running
/// launcher process.
///
/// WHY A RIG AND NOT A TEST. Every scenario in LauncherUpdateOwnerTests drives the pass through fakes
/// for the process list, the stop and the start - which is right for the decisions, and says nothing
/// at all about the three production delegates that actually touch the machine. DefaultStopProcess,
/// DefaultStartLauncher (including the CC_DIRECTOR_ROOT it hands the child and the --managed argument)
/// and the witness reading a genuinely new process had never once executed against a real launcher.
/// This runs them - and the first run found a real defect in exactly that environment handling.
///
/// WHAT IT PROVES AND WHAT IT DOES NOT - read this before quoting it as the end-to-end proof:
///
///   PROVED HERE: an installed launcher running from an isolated root is stopped for real, its binary
///   is replaced for real, a new process is started for real by DefaultStartLauncher, and the new
///   build is WITNESSED - registered, alive, and holding a live command surface at the isolated
///   root's own signal names.
///
///   NOT PROVED HERE: the case that actually matters most on a real machine - a launcher that is the
///   PARENT of a live Director. On Windows the installed launcher is the Director's parent process,
///   so the swap stops this process's own parent, and the no-orphan invariant (the Director keeps
///   running across the swap and the new launcher re-adopts it rather than starting a second one) is
///   the whole reason the stop refuses to kill a process tree. The rig's launcher supervises no
///   Director, so that invariant is NOT exercised by this program. It is asserted only by
///   LauncherUpdateOwnerTests, through a fake.
///
/// The isolation itself rests on a fact established on 2026-09-06 and recorded on issue #2719: a
/// launcher started with CC_DIRECTOR_ROOT pointing elsewhere serves THAT root completely - it
/// registers under it, derives its own root key from it, and its instance guard positively refuses to
/// act on a Director that is not its own. That is what lets this run beside a live fleet. The rig
/// asserts that separation rather than assuming it: it refuses to start if the root it was given is
/// the real one, and it checks afterwards that the machine's real launcher was left untouched.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: launcher-swap-proof <isolated-root> [expected-new-version] [verdict-file]");
            return 2;
        }

        var root = Path.GetFullPath(args[0]);
        var expectedNewVersion = args.Length > 1 ? args[1] : null;
        // Named by the CALLER, so each run has its own. A single shared path in the temporary
        // directory meant two runs side by side could overwrite one another's answer, and a failing
        // run could read another run's PASS.
        _verdictPath = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
            ? Path.GetFullPath(args[2])
            : Path.Combine(Path.GetTempPath(), "launcher-swap-proof.verdict.txt");

        var realRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cc-director");
        if (string.Equals(Path.TrimEndingDirectorySeparator(root),
                          Path.TrimEndingDirectorySeparator(realRoot), StringComparison.OrdinalIgnoreCase))
        {
            // The one refusal that matters. This program stops launchers and replaces binaries; pointed
            // at the real root it would do that to the launcher supervising the live fleet.
            Console.Error.WriteLine($"REFUSING: {root} is this machine's REAL cc-director root.");
            return 2;
        }

        var realLauncherBefore = new LauncherWitness(realRoot).Read();

        var witness = new LauncherWitness(root);
        var owner = new LauncherUpdateOwner(
            root,
            // The rig's launcher supervises no Director, so there is no instance for this to be about.
            // Stated rather than defaulted, and reported below so nobody reads it as the no-orphan
            // invariant having been exercised.
            directorStillHoldsItsInstance: () => true,
            witnessTimeout: TimeSpan.FromMinutes(2));

        Console.WriteLine($"root         = {root}");
        Console.WriteLine($"signal       = {witness.CommandSignalName}");

        var before = witness.Read();
        Console.WriteLine("--- BEFORE ---");
        Report(before);
        var staged = owner.FindStagedUpdate(out var why);
        Console.WriteLine($"staged       = {staged?.Version ?? "(none)"} why=[{why}]");

        if (!before.Witnessed)
        {
            Console.Error.WriteLine("FAIL: the rig's own launcher is not witnessed before the swap, so the swap "
                                    + "would be measured from a broken starting point.");
            WriteVerdict("FAIL: the rig's own launcher was not witnessed before the swap");
            return 1;
        }
        if (staged is null)
        {
            Console.Error.WriteLine("FAIL: nothing is staged, so there is no swap to prove.");
            WriteVerdict("FAIL: nothing was staged, so there was no swap to prove");
            return 1;
        }

        Console.WriteLine("--- SWAPPING (real stop, real binary replacement, real start) ---");
        var result = await owner.RunOnceAsync();
        Console.WriteLine($"decision     = {result.Decision}");
        Console.WriteLine($"message      = {result.Message}");
        foreach (var step in result.Steps) Console.WriteLine($"  step: {step}");

        var after = witness.Read();
        Console.WriteLine("--- AFTER ---");
        Report(after);

        var failures = new List<string>();

        if (result.Decision != LauncherUpdateDecision.Applied)
            failures.Add($"decision was {result.Decision}, expected Applied");

        if (!after.Witnessed)
            failures.Add("the new launcher is NOT witnessed - the whole point is that a started process is "
                         + "not the proof; it must be registered, alive AND commandable");

        if (after.CommandSurface != LauncherCommandSurface.Present)
            failures.Add($"the new launcher's command surface is {after.CommandSurface}, expected Present");

        if (after.Pid == before.Pid)
            failures.Add($"the launcher process id did not change ({after.Pid}) - nothing was actually replaced");

        if (expectedNewVersion is not null
            && (after.Version is null || !after.Version.StartsWith(expectedNewVersion, StringComparison.Ordinal)))
            failures.Add($"the running launcher reports {after.Version ?? "(none)"}, expected {expectedNewVersion}");

        // THE ISOLATION ASSERTION. The rig runs beside a live fleet, so "it worked" is not enough - it
        // must also be true that nothing outside the isolated root moved.
        var realLauncherAfter = new LauncherWitness(realRoot).Read();
        Console.WriteLine("--- THE MACHINE'S REAL LAUNCHER (must be untouched) ---");
        Report(realLauncherAfter);
        if (realLauncherBefore.Pid != realLauncherAfter.Pid || realLauncherBefore.Version != realLauncherAfter.Version)
            failures.Add($"THE REAL LAUNCHER MOVED: was pid {realLauncherBefore.Pid} {realLauncherBefore.Version}, "
                         + $"now pid {realLauncherAfter.Pid} {realLauncherAfter.Version}. The isolation this rig "
                         + "depends on does not hold - STOP and write that up; it is a bigger finding than the "
                         + "proof would have been.");

        Console.WriteLine();
        Console.WriteLine("NOT PROVED BY THIS RUN: the rig's launcher supervised no Director, so a launcher that is "
                          + "a LIVE DIRECTOR'S PARENT has still never been swapped, and the no-orphan invariant is "
                          + "asserted only through a fake.");

        if (failures.Count > 0)
        {
            Console.Error.WriteLine();
            foreach (var f in failures) Console.Error.WriteLine($"FAIL: {f}");
            WriteVerdict("FAIL: " + string.Join(" | ", failures));
            return 1;
        }

        Console.WriteLine("PASS: the launcher was stopped, replaced and restarted, and the NEW build was witnessed "
                          + "alive and commandable on the isolated root.");
        WriteVerdict("PASS");
        return 0;
    }

    /// <summary>
    /// The verdict, written where the calling script can read it.
    ///
    /// The exit code alone was not enough, and the reason is a Windows PowerShell quirk with teeth:
    /// Start-Process -PassThru WITHOUT -Wait returns a process object that does not retain the handle,
    /// so ExitCode reads back EMPTY once the process is gone - and -Wait cannot be used, because with
    /// redirected output it waits for the streams to close and the launcher this proof deliberately
    /// leaves running has inherited them. So the run reported a FAILED proof on a run that passed.
    /// A file the driver writes on its way out is decided by the driver, not by how it was invoked.
    /// </summary>
    private static void WriteVerdict(string verdict)
    {
        try
        {
            File.WriteAllText(_verdictPath, verdict);
            Console.WriteLine($"verdict written to {_verdictPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not write the verdict file: {ex.Message}");
        }
    }

    /// <summary>Where the verdict is written - supplied per run by the calling script, so a verdict
    /// from a concurrent run can never be read as this one's result.</summary>
    private static string _verdictPath = "";

    private static void Report(LauncherWitnessReading r)
    {
        Console.WriteLine($"registered   = {r.Registered}");
        Console.WriteLine($"alive        = {r.ProcessAlive} (pid {r.Pid})");
        Console.WriteLine($"version      = {r.Version ?? "(none)"}");
        Console.WriteLine($"surface      = {r.CommandSurface}");
        Console.WriteLine($"WITNESSED    = {r.Witnessed}");
        Console.WriteLine($"detail       = {r.Detail}");
    }
}
