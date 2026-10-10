using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Serializes runs of THIS test assembly for one user on one machine, automatically, before any test runs.
/// The exact scope is stated below and is deliberately narrower than "machine-wide" - do not widen the
/// wording without widening the mechanism.
///
/// WHY THIS EXISTS - READ THIS BEFORE YOU DELETE IT.
///
/// This suite cannot tolerate two concurrent runs on one machine. Overlapping runs kill the test host:
/// they report failures in areas nobody touched, and frequently end with no summary line at all, which is
/// the shape of a crashed host rather than a failed assertion. On 2026-07-19 four runs overlapped and every
/// one of the four had to be discarded. Corrupted test evidence is worse than no test evidence, because
/// somebody acts on it.
///
/// Several people and several agents work in this repository at once, each in their own working tree, and
/// each one runs "dotnet test src/CcDirector.Gateway.Tests". Telling them not to overlap DOES NOT WORK, and
/// the reason it does not work is the whole design argument for this file: a runner can only observe its
/// OWN run. Each one honours the rule individually while the aggregate still breaks it. A rule that
/// requires global knowledge cannot be obeyed by an actor holding only local knowledge. A lock that a
/// runner must REMEMBER to take is the same convention wearing a new name - somebody who was never briefed,
/// or who arrives tomorrow, walks straight past it. We have direct evidence of that too: a full suite ran
/// on this machine from a working tree with no owning session at all, so there was nobody to tell.
///
/// So acquisition is AUTOMATIC and lives inside the test process. A plain "dotnet test" serializes without
/// the caller knowing this file exists. That is the entire point, and it is the property to preserve if you
/// ever rework this.
///
/// HOW OWNERSHIP IS DECIDED: BY THE OPERATING SYSTEM, NEVER BY METADATA.
///
/// The lock is a file opened for writing with <see cref="FileShare.Read"/> and held open for the lifetime
/// of the process. A second run's open fails, because its request to write is not among the shares the
/// first run granted. Readers are still admitted, which is how a blocked run tells you WHO is holding it.
///
/// Two consequences, both deliberate:
///
///  1. A HOLDER THAT DIES NEEDS NO CLEANUP LOGIC. Crash, kill, or the known file-system-watcher dispose
///     race that can take this test host down mid-run - in every case the operating system closes the
///     handle, and the next run simply acquires. There is no stale-detection code here because there is
///     nothing for it to do. That matters: a lock that could wedge permanently would stop the Gateway
///     suite for every working tree on the machine after the first crash, trading a corrupted-evidence
///     problem for a total-stoppage problem.
///
///  2. A HOLDER THAT IS ALIVE BUT STUCK MAKES THIS RUN FAIL LOUDLY - it does not make this run start.
///     An earlier draft broke the lock once the holder passed an age cap. That was wrong, and the reason
///     is worth keeping: an age cap cannot distinguish "wedged" from "merely slow", so breaking on age
///     starts a second suite next to a live first one, which is exactly the corruption this file exists to
///     prevent. Nothing here ever takes a lock away from a living process. Past
///     <see cref="MaxWait"/> the run stops with an error naming the holder, and a human or agent decides.
///     A loud failure is recoverable. A silent second concurrent suite is corrupted evidence that looks
///     clean.
///
/// The process id, process start time and session identifier written into the file are DIAGNOSTICS ONLY.
/// They exist so a blocked run can name its blocker - a blocked run that cannot say who is blocking it is
/// indistinguishable from a hang, and somebody will kill the wrong thing. They never decide ownership.
///
/// THE GUARANTEE IS PER-USER-PER-MACHINE. Not machine-wide - say it precisely, because someone will
/// eventually rely on the words. Two runs by the SAME operating-system user on the SAME machine cannot
/// overlap. Two runs by DIFFERENT users on one machine are NOT serialized by this, and on Windows they
/// cannot be, because the lock lives under the per-user local application data directory.
///
/// That scope was chosen, not overlooked. Every process that actually contends here runs as the same user:
/// each agent is a subprocess of a session running under that account, and that was verified rather than
/// assumed. A machine-shared home would have to sit somewhere writable by every account, which brings
/// permission and cleanup failures of its own - and those failures are themselves wedge risks, which is the
/// exact class of problem this mechanism exists to remove. Buying protection against a collision we do not
/// have, at the cost of new ways to jam, is a bad trade.
///
/// REVISIT THIS if a second operating-system user ever runs this suite on one machine - a service account,
/// a continuous-integration agent alongside an interactive login, a second person on a shared box. At that
/// point the scope is genuinely too narrow and the home must move somewhere shared, with the permission
/// model worked out deliberately. Until then, this is the observed contention boundary.
///
/// EVERY RUN OF THIS ASSEMBLY IS SERIALIZED, FILTERED OR NOT. That is a feature, not an oversight, and it
/// is the first thing somebody will try to "optimise" - a filtered run looks small and harmless, so surely
/// it can be let through. It cannot, because nobody can show that it is harmless. A filtered run of this
/// assembly still loads the whole assembly, still boots real hosts, still touches shared machine state, and
/// is still exposed to the host-crash mode that has never been proven purely intra-process. Each test class
/// does redirect its storage root to a unique temporary path, so roots do not collide - but that is the
/// only isolation anybody has actually demonstrated, and "no collision was found" is not "no collision
/// exists". The lock takes the strong position deliberately: serialize everything, and let anyone who wants
/// an exemption produce the evidence first. This is also why the acquisition sits in a module initializer
/// rather than anywhere test-shaped - a module initializer cannot see the filter, and therefore cannot be
/// talked into making an exception it has no basis for.
///
/// WHY A MODULE INITIALIZER. It runs on assembly load, before any test, it is independent of the xUnit
/// version (assembly-level fixtures are a v3 feature and this project is on xunit 2.9), and it works for
/// every runner - "dotnet test", the development-environment runner, and anything else that loads this
/// assembly. It also suits a lock held for process lifetime exactly, because there is no teardown hook to
/// get right. It is the same form <see cref="TestStorageRootRedirect"/> already uses in this assembly, for
/// the same reason: there is no call site to forget. It is pinned by
/// <see cref="GatewayTestSuiteLockTests"/>, because a guard that silently fails to run leaves no trace.
/// </summary>
internal static class GatewayTestSuiteLock
{
    /// <summary>
    /// How long this run queues behind a live holder before failing loudly.
    ///
    /// TWO MINUTES, SINCE OCTOBER 2026. It was forty-five, sized for a queue of four full runs, and the
    /// queue is what went wrong: between 8 and 10 October nineteen release-gate runs each waited the full
    /// forty-five minutes and then executed nothing - fourteen hours of gate time that proved nothing,
    /// with up to seven gates overlapping. A queue hides the conflict it is in. The gate script now
    /// takes a release-gate lock BEFORE it builds and refuses at once when one is held
    /// (scripts/gate-lock.ps1), so by the time this initializer runs the only holder it can meet is a
    /// hand-run "dotnet test" of this suite that started in the last few minutes. Two minutes lets a
    /// short filtered run of that kind finish; anything longer is a conflict to show, not to hide.
    /// Phase 2 of the release-process work removes this lock altogether.
    ///
    /// The cost of the wait being too short is a clear, re-runnable error; the cost of breaking the
    /// lock instead would be a corrupted run. That asymmetry is why this number can be wrong without
    /// being dangerous.
    /// </summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    /// <summary>Pause between acquisition attempts while queued behind a live holder.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>How often to repeat the "still waiting" line, so a queued run never looks like a hang.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    /// <summary>Exit code used when this run refuses to start because a live holder never let go.</summary>
    internal const int BlockedExitCode = 99;

    /// <summary>Exit code used when the lock location itself is unusable - a setup failure, not contention.</summary>
    internal const int SetupFailureExitCode = 98;

    /// <summary>
    /// The qualification bypass (issue #1156 step 4). The soak that qualifies removing this lock has to run
    /// several ordinary suite processes CONCURRENTLY - the very thing the lock forbids - so the
    /// qualification script, and only it, asks for a bypass by setting this variable to
    /// <see cref="QualificationToken"/> in each child process it launches.
    /// </summary>
    internal const string QualificationEnvVar = "CC_GATEWAY_TEST_LOCK_QUALIFICATION";

    /// <summary>
    /// The exact value <see cref="QualificationEnvVar"/> must carry. A specific sentence rather than "1",
    /// so a stray truthy value copied between shells cannot silently disable the lock.
    /// </summary>
    internal const string QualificationToken = "isolated-worktree-soak";

    /// <summary>
    /// The live-proof connection variables the bypass fails CLOSED on. The stats write-path proofs point at
    /// one named database rather than deriving a per-run one, and the gateway-database live proofs use the
    /// production variable outright - two qualification processes sharing either would corrupt each other
    /// and report failures nobody caused, which is precisely the false evidence a soak must never produce.
    /// The qualification script clears these for its children; this check is what makes forgetting that a
    /// loud stop instead of a quiet lie.
    /// </summary>
    internal static readonly string[] LiveProofEnvVars =
    {
        "CC_GATEWAY_TEST_PG_CONNECTION",
        "CC_GATEWAY_TEST_PG_STATS_CONNECTION",
        "CC_GATEWAY_DB_CONNECTION",
    };

    /// <summary>True when this run was admitted through the qualification bypass rather than the lock.
    /// The two lock-behaviour tests read it: the serialization property is DELIBERATELY suspended in a
    /// qualification run, and they assert that state rather than false-failing the soak.</summary>
    internal static bool QualificationBypassActive { get; private set; }

    /// <summary>How the run may proceed, ruled from values alone so both branches are unit-testable.</summary>
    internal enum QualificationRuling
    {
        /// <summary>No bypass requested: take the lock as always.</summary>
        NotRequested,

        /// <summary>Requested with the exact token and no live-proof variable set: run without the lock.</summary>
        Bypass,

        /// <summary>Requested while a live-proof variable is set: stop the process, run nothing.</summary>
        RefuseLiveProof,

        /// <summary>The variable is set but carries the wrong value. Stop the process: silently taking the
        /// locked path instead would serialize the whole soak, and thousands of "clean" host starts that
        /// never actually overlapped would certify nothing while looking like proof.</summary>
        RefuseWrongToken,
    }

    /// <summary>
    /// Pure ruling over the qualification request - reads nothing ambient, so a test can hand it every
    /// combination without mutating process state (the same rule <see cref="ComputeLockFilePath"/> follows).
    /// </summary>
    internal static QualificationRuling RuleOnQualification(
        string? qualificationValue, IReadOnlyList<(string Name, string? Value)> liveProofValues)
    {
        if (string.IsNullOrWhiteSpace(qualificationValue))
            return QualificationRuling.NotRequested;

        if (!string.Equals(qualificationValue, QualificationToken, StringComparison.Ordinal))
            return QualificationRuling.RefuseWrongToken;

        foreach (var (_, value) in liveProofValues)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return QualificationRuling.RefuseLiveProof;
        }

        return QualificationRuling.Bypass;
    }

    /// <summary>The file name every run of this assembly contends for, whatever working tree it was built
    /// from.</summary>
    private const string LockFileName = "gateway-test-suite.lock";

    /// <summary>The lock file every run of this assembly contends for.</summary>
    internal static string LockFilePath { get; } = ComputeLockFilePath(ReadAmbient());

    /// <summary>
    /// Everything the lock path could conceivably be derived from, captured as VALUES.
    ///
    /// The temporary-directory variables are carried here despite the derivation never using them, and that
    /// is the point: it lets a test hand over a hostile environment and assert the answer does not move.
    /// A property proved by passing arguments needs no global state mutated to prove it.
    /// </summary>
    internal readonly record struct AmbientEnvironment(
        string? Temp,
        string? Tmp,
        string? TmpDir,
        string? LocalAppDataVariable,
        string LocalApplicationDataFolder,
        string UserName,
        bool IsWindows);

    /// <summary>
    /// The ONE place that touches process-global state. Everything downstream is a pure function of what
    /// this returns, so nothing else in the lock can be perturbed by whatever else is running in the process.
    /// </summary>
    internal static AmbientEnvironment ReadAmbient() => new(
        Temp: Environment.GetEnvironmentVariable("TEMP"),
        Tmp: Environment.GetEnvironmentVariable("TMP"),
        TmpDir: Environment.GetEnvironmentVariable("TMPDIR"),
        LocalAppDataVariable: Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        LocalApplicationDataFolder: Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        UserName: Environment.UserName,
        IsWindows: OperatingSystem.IsWindows());

    /// <summary>
    /// Derives the lock path from a location the PROCESS ENVIRONMENT CANNOT MOVE. A pure function of its
    /// argument - it reads no ambient state, which is what makes the claim testable rather than assertable.
    ///
    /// This is the difference between a lock and a decoration, and it was got wrong first time round. The
    /// original used <see cref="Path.GetTempPath"/>, which reads TEMP and TMP from the environment. Two runs
    /// launched with different TEMP values computed different lock files, so neither ever saw the other:
    /// both printed "Acquired" in the same second and both ran concurrently. That was demonstrated, not
    /// theorised. It is also the worst possible way to fail, because the environments most likely to differ
    /// are precisely the ones this exists to serialize - different agents, different shells, different
    /// working trees, a scheduled task against an interactive session.
    ///
    /// The rule this now follows: a lock's identity may depend on the MACHINE and the USER, never on
    /// anything the caller can set. On Windows the shell folder API supplies the per-user local application
    /// data directory from the user profile, and ignores the LOCALAPPDATA environment variable even when it
    /// is overridden - measured, not assumed. Elsewhere the path is a fixed literal, because
    /// <see cref="Path.GetTempPath"/> reads TMPDIR and the folder API reads XDG_DATA_HOME and HOME; the user
    /// name goes into the file name instead, since a shared directory needs the per-user split somewhere.
    ///
    /// Because the platform is an INPUT rather than something read from the running process,
    /// <see cref="GatewayTestSuiteLockTests"/> exercises BOTH branches on either platform - so the Unix
    /// branch is covered by a Windows developer's run, and not left to be discovered by continuous
    /// integration on a machine that developer never sees.
    /// </summary>
    internal static string ComputeLockFilePath(AmbientEnvironment ambient)
    {
        if (!ambient.IsWindows)
        {
            // Built by hand rather than with Path.Combine, which would join with a backslash when this
            // branch is evaluated on Windows and produce a path no Unix machine would ever resolve. The
            // separator belongs to the TARGET platform, not the running one.
            return "/tmp/cc-director-" + ambient.UserName + "-" + LockFileName;
        }

        if (string.IsNullOrWhiteSpace(ambient.LocalApplicationDataFolder))
        {
            // Nothing to fall back to that would still be a lock: every alternative is environment-settable,
            // which is the defect this method exists to close. Better to stop than to serialize nothing.
            throw new InvalidOperationException(
                "Cannot locate the per-user local application data directory, so the per-user Gateway "
                + "test lock has no environment-independent home. Running without it would let two Gateway "
                + "test runs execute concurrently and corrupt each other. See GatewayTestSuiteLock.");
        }

        // A clearly-namespaced subdirectory rather than sitting beside product state. The parent directory
        // holds the owner's live fleet data - missions, cron jobs, the key vault - and something enumerating
        // it should never have to wonder whether a stray ".lock" file is product data or test scaffolding.
        // The name says what it is and who put it there.
        return Path.Combine(
            ambient.LocalApplicationDataFolder, "cc-director", "test-locks", LockFileName);
    }

    /// <summary>A copy of everything this run printed while acquiring, kept next to the lock so a human
    /// investigating a blocked machine can read the history without a console to look at.</summary>
    private static string LogFilePath => LockFilePath + ".log";

    /// <summary>
    /// The held handle. Static and never disposed ON PURPOSE: the lock lasts as long as the process, and
    /// the operating system closes it however the process ends. Nothing here has to run on a particular
    /// thread, so the thread-affinity trap that a named <see cref="Mutex"/> would have brought does not
    /// arise at all.
    /// </summary>
#pragma warning disable CA2213
    private static FileStream? _held;
#pragma warning restore CA2213

    /// <summary>True once this run owns the lock.</summary>
    internal static bool IsHeld => _held is not null;

    [ModuleInitializer]
    internal static void Acquire()
    {
        var liveProofValues = new (string Name, string? Value)[LiveProofEnvVars.Length];
        for (var i = 0; i < LiveProofEnvVars.Length; i++)
            liveProofValues[i] = (LiveProofEnvVars[i], Environment.GetEnvironmentVariable(LiveProofEnvVars[i]));

        switch (RuleOnQualification(Environment.GetEnvironmentVariable(QualificationEnvVar), liveProofValues))
        {
            case QualificationRuling.Bypass:
                QualificationBypassActive = true;
                Say($"[gateway-test-lock] QUALIFICATION BYPASS (issue #1156 step 4): this run was launched "
                    + $"by the qualification script and runs WITHOUT the per-user lock, concurrently with "
                    + $"its sibling processes, to qualify removing the lock. pid {Environment.ProcessId}. "
                    + $"If you are seeing this outside scripts/test-qualification.ps1, unset "
                    + $"{QualificationEnvVar} - an ordinary run must never bypass the lock.");
                return;

            case QualificationRuling.RefuseLiveProof:
                var offending = string.Join(", ", Array.ConvertAll(
                    Array.FindAll(liveProofValues, v => !string.IsNullOrWhiteSpace(v.Value)), v => v.Name));
                Say($"[gateway-test-lock] *** QUALIFICATION REFUSED. {QualificationEnvVar} is set, but a "
                    + $"live-proof connection variable is also set ({offending}). Concurrent qualification "
                    + $"processes sharing a live database corrupt each other and produce false soak "
                    + $"evidence. NO TESTS WILL RUN. Clear those variables in the qualification children "
                    + $"and run again. ***");
                Console.Out.Flush();
                Console.Error.Flush();
                Environment.Exit(SetupFailureExitCode);
                return;

            case QualificationRuling.RefuseWrongToken:
                Say($"[gateway-test-lock] *** QUALIFICATION REFUSED. {QualificationEnvVar} is set but does "
                    + $"not carry the exact token '{QualificationToken}'. Falling back to the locked path "
                    + $"would silently serialize the soak and certify nothing, so NO TESTS WILL RUN. Fix "
                    + $"the value or unset the variable. ***");
                Console.Out.Flush();
                Console.Error.Flush();
                Environment.Exit(SetupFailureExitCode);
                return;
        }

        var started = DateTime.UtcNow;
        var lastProgress = DateTime.MinValue;
        var announcedWait = false;

        EnsureLockLocationUsable();

        while (true)
        {
            if (TryOpenExclusively())
            {
                Say($"[gateway-test-lock] Acquired the per-user Gateway test lock. Starting the run. "
                    + $"Lock file: {LockFilePath}");
                return;
            }

            var holder = ReadHolder();

            if (!announcedWait)
            {
                announcedWait = true;
                lastProgress = DateTime.UtcNow;
                Say($"[gateway-test-lock] WAITING. Another run of CcDirector.Gateway.Tests holds the "
                    + $"per-user test lock, and this suite is destroyed by concurrent runs, so this run "
                    + $"is queued rather than started. THIS IS NOT A HANG. Holder: {Describe(holder)}. "
                    + $"Lock file: {LockFilePath}. When the holder's process ends the lock is released by "
                    + $"the operating system and this run starts on its own. If the holder is still alive "
                    + $"after {MaxWait.TotalMinutes:0} minutes this run FAILS rather than running "
                    + $"alongside it.");
            }
            else if (DateTime.UtcNow - lastProgress >= ProgressInterval)
            {
                lastProgress = DateTime.UtcNow;
                Say($"[gateway-test-lock] Still waiting after {(DateTime.UtcNow - started).TotalSeconds:0}s. "
                    + $"Holder: {Describe(holder)}.");
            }

            if (DateTime.UtcNow - started >= MaxWait)
            {
                Say($"[gateway-test-lock] *** REFUSING TO RUN. A live process has held the per-user "
                    + $"Gateway test lock for {MaxWait.TotalMinutes:0} minutes. NO TESTS WILL RUN. This run "
                    + $"stops instead of starting alongside it, because two concurrent runs of this suite "
                    + $"corrupt each other's results, and a run that quietly overlapped would report "
                    + $"failures nobody caused. Holder: {Describe(holder)}. Lock file: {LockFilePath}. "
                    + $"Decide whether that holder is genuinely still working; if it is stuck, end that "
                    + $"process and run this again. ***");

                // Environment.Exit rather than an exception: an exception thrown from a module initializer
                // surfaces as a type-initialization failure on whichever test happened to touch this type
                // first, which reads as an unrelated test failure. Stopping the process is unambiguous -
                // no test ran, and the reason is the last thing on the console.
                Console.Out.Flush();
                Console.Error.Flush();
                Environment.Exit(BlockedExitCode);
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>
    /// One acquisition attempt. Exclusivity is the operating system's answer to this open call - the file's
    /// contents are written only AFTER the handle is granted, and are never consulted to decide anything.
    /// </summary>
    private static bool TryOpenExclusively()
    {
        FileStream stream;
        try
        {
            // FileAccess.Write with FileShare.Read: a second run asking to write is refused, because write
            // is not among the shares this handle grants. Readers are admitted so a blocked run can read
            // the diagnostics below and name its blocker.
            stream = new FileStream(
                LockFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        }
        catch (IOException ex) when (IsSharingContention(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            // ONLY a sharing conflict means "somebody else is running". Everything else - a permission
            // failure, a read-only file, a directory at the path, a broken or full disk - is a setup fault,
            // and treating it as contention is how the wedge we designed this to avoid comes back through
            // the error path: the run would wait the full timeout while naming a holder that does not
            // exist, then the next run would do the same, forever. So these stop the run at once, saying
            // what actually happened.
            FailSetup("cannot open the lock file", ex);
            throw; // unreachable: FailSetup ends the process. Present so the compiler sees a terminal path.
        }

        _held = stream;
        WriteDiagnostics(stream);
        return true;
    }

    /// <summary>
    /// Distinguishes "another process holds this file" from every other input/output failure.
    ///
    /// On Windows the operating system names it exactly, so the test is exact: a sharing violation or a
    /// lock violation, and nothing else. Elsewhere the errno mapping for an advisory-lock conflict is not
    /// something this code has verified on the platform in question, and guessing at it would be the same
    /// class of mistake as the temp-path defect - so instead the question is answered with EVIDENCE: a live
    /// holder grants readers access, so if the file can still be opened for reading, somebody is holding it.
    /// If it cannot even be read, this is not contention and must not be reported as a holder.
    /// </summary>
    private static bool IsSharingContention(IOException ex)
    {
        const int SharingViolation = 32;
        const int LockViolation = 33;

        if (OperatingSystem.IsWindows())
        {
            var code = ex.HResult & 0xFFFF;
            return code is SharingViolation or LockViolation;
        }

        try
        {
            using var probe = new FileStream(
                LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks the lock's home before the acquisition loop starts, so the common setup faults are reported
    /// precisely rather than as whatever the open call happens to throw. Each of these would otherwise have
    /// been indistinguishable from a live holder.
    /// </summary>
    private static void EnsureLockLocationUsable()
    {
        var directory = Path.GetDirectoryName(LockFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            FailSetup($"the lock path '{LockFilePath}' has no containing directory", exception: null);
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            FailSetup($"cannot create the lock directory '{directory}'", ex);
            return;
        }

        if (Directory.Exists(LockFilePath))
        {
            FailSetup(
                $"a DIRECTORY sits at the lock file path '{LockFilePath}', so the lock file can never be "
                + "opened. Remove that directory.",
                exception: null);
            return;
        }

        try
        {
            if (File.Exists(LockFilePath)
                && (File.GetAttributes(LockFilePath) & FileAttributes.ReadOnly) != 0)
            {
                FailSetup(
                    $"the lock file '{LockFilePath}' is marked READ-ONLY, so it can never be opened for "
                    + "writing. Clear the read-only attribute.",
                    exception: null);
            }
        }
        catch (Exception ex)
        {
            FailSetup($"cannot inspect the lock file '{LockFilePath}'", ex);
        }
    }

    /// <summary>
    /// Stops the run because the lock's home is broken. Deliberately NOT a wait and NOT a retry: a setup
    /// fault does not clear on its own, so retrying it produces a run that hangs for the full timeout and
    /// then fails for the wrong reason. This says what is wrong and stops.
    /// </summary>
    private static void FailSetup(string what, Exception? exception)
    {
        var detail = exception is null ? "" : $" Underlying failure: {exception.GetType().Name}: {exception.Message}.";
        Say($"[gateway-test-lock] *** CANNOT SET UP THE PER-USER GATEWAY TEST LOCK: {what}.{detail} "
            + "NO TESTS WILL RUN. This is NOT another run holding the lock - it is a fault in the lock's "
            + "location that will not clear by waiting, so this run stops immediately rather than blocking "
            + "and then blaming a holder that does not exist. Fix the path above and run again. ***");
        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(SetupFailureExitCode);
    }

    /// <summary>
    /// Writes who holds the lock, for a human to read. Purely informational - see the class remarks. Kept
    /// flushed to disk so a waiter reading the file sees it immediately rather than after the holder exits.
    /// </summary>
    private static void WriteDiagnostics(FileStream stream)
    {
        using var self = Process.GetCurrentProcess();
        var text = string.Join(Environment.NewLine, new[]
        {
            "processId=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "processStartUtc=" + self.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            "acquiredUtc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "session=" + SessionIdentifier(),
            "machine=" + Environment.MachineName,
            "user=" + Environment.UserName,
            "directory=" + Environment.CurrentDirectory,
        }) + Environment.NewLine;

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        stream.SetLength(0);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Who to go and talk to. Opened read-only, sharing everything, so reading never disturbs the holder
    /// and never itself becomes a lock.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ReadHolder()
    {
        try
        {
            using var stream = new FileStream(
                LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var split = line.IndexOf('=');
                if (split > 0)
                    fields[line[..split]] = line[(split + 1)..];
            }

            return fields.Count == 0 ? null : fields;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Describe(IReadOnlyDictionary<string, string>? holder)
    {
        if (holder is null)
            return "(the lock file carries no readable diagnostics)";

        var pid = holder.TryGetValue("processId", out var p) ? p : "unknown";
        var since = holder.TryGetValue("acquiredUtc", out var a) ? a : "unknown";
        var startedAt = holder.TryGetValue("processStartUtc", out var s) ? s : "unknown";
        var session = holder.TryGetValue("session", out var ss) ? ss : "unknown";
        var dir = holder.TryGetValue("directory", out var d) ? d : "unknown";

        var held = "";
        if (DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
            held = $" ({(DateTime.UtcNow - t.ToUniversalTime()).TotalSeconds:0}s ago)";

        return $"process {pid}, started {startedAt}, holding since {since}{held}, "
            + $"owner {session}, working directory {dir}";
    }

    /// <summary>
    /// Identifies the run for a human reading a blocked run's error message. Every fleet-launched session
    /// carries CC_SESSION_ID, which is the case that matters - it names a session somebody can go and talk
    /// to. A run started outside the fleet has no session to name, and says so rather than inventing one;
    /// the working directory recorded alongside this is what identifies those.
    /// </summary>
    private static string SessionIdentifier()
    {
        var id = Environment.GetEnvironmentVariable("CC_SESSION_ID");
        return string.IsNullOrWhiteSpace(id)
            ? "(no session identifier in this run's environment - identify it by the working directory below)"
            : "cc-director session " + id;
    }

    /// <summary>
    /// Says it on standard output, standard error, the attached terminal, and into a log beside the lock
    /// file.
    ///
    /// Four channels because the message that matters most is the one printed while a run is BLOCKED, and
    /// a waiting message nobody sees is a hang - somebody will kill it, or kill the holder. Measured, not
    /// assumed: "dotnet test" launches the test host as a child with its standard streams redirected, and
    /// at the DEFAULT console verbosity it relays none of that output. So the terminal is written
    /// DIRECTLY as well (CONOUT$ on Windows, /dev/tty elsewhere), which the test host can do because it
    /// still inherits the console its parent is attached to. The log file covers the remaining case: a run
    /// with no terminal at all, such as a scheduled or continuous-integration invocation, where a human
    /// arrives afterwards asking what blocked.
    /// </summary>
    private static void Say(string message)
    {
        var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId}: {message}";
        Console.Out.WriteLine(stamped);
        Console.Out.Flush();
        Console.Error.WriteLine(stamped);
        Console.Error.Flush();
        SayOnTerminal(stamped);
        try
        {
            File.AppendAllText(LogFilePath, stamped + Environment.NewLine);
        }
        catch (Exception)
        {
            // Every failure, not merely IOException. This previously caught IOException alone, which meant
            // an unwritable or read-only log file threw UnauthorizedAccessException out of a DIAGNOSTIC
            // write - after the lock was already acquired - and would have aborted every future run. A
            // channel whose only job is to explain a problem must never be able to cause one. The console
            // channels above already carry the message; this file is a convenience for reading a machine
            // afterwards with no console attached.
        }
    }

    /// <summary>Writes straight to the inherited terminal, bypassing the parent runner's stream capture.</summary>
    private static void SayOnTerminal(string stamped)
    {
        var device = OperatingSystem.IsWindows() ? "CONOUT$" : "/dev/tty";
        try
        {
            using var terminal = new StreamWriter(
                new FileStream(device, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            terminal.WriteLine(stamped);
            terminal.Flush();
        }
        catch (Exception)
        {
            // No terminal is attached (a scheduled run, a continuous-integration agent, a redirected
            // pipe with no console). The standard streams and the log file already carry the message.
        }
    }
}
