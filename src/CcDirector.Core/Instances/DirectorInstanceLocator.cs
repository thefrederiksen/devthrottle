using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Instances;

/// <summary>How the search for the supervised Director ended.</summary>
public enum DirectorResolution
{
    /// <summary>Exactly one live Director owns the supervised instance. It is named in the lookup.</summary>
    Running,

    /// <summary>
    /// No live Director owns the supervised instance, AND everything that claimed it was read and ruled
    /// out. It is a positive finding, not the absence of one - see <see cref="Unknown"/>.
    /// </summary>
    NotRunning,


    /// <summary>
    /// More than one live process claims the supervised instance. NOTHING may be done to any of them:
    /// see the class comment on <see cref="DirectorInstanceLocator"/>.
    /// </summary>
    Ambiguous,

    /// <summary>
    /// A live process holds the supervised instance, but it is NOT running the image this launcher
    /// supervises. Something is there, so nothing new may be started on top of it - and it must not be
    /// stopped, force-killed or updated over, because it is not this launcher's process to end.
    ///
    /// This is a distinct answer from <see cref="Ambiguous"/> on purpose. Ambiguous means the launcher
    /// cannot tell WHICH of several claimants is its Director; this means it can tell perfectly well,
    /// and the answer is "none of them". A log line that said "ambiguous" about a single claimant would
    /// send the next person looking for a second process that does not exist.
    /// </summary>
    NotSupervised,

    /// <summary>
    /// Something claims the supervised instance and it could not be READ or CERTIFIED, so whether a
    /// Director is running here is unknown. Nothing survived to be named, but nothing was ruled out
    /// either.
    ///
    /// WHY THIS IS NOT <see cref="NotRunning"/>, WHICH IS THE DEFECT IT REMOVES (issue #2730). A
    /// registration that will not parse, and a live process that will not say when it started, were both
    /// SKIPPED - and skipping is right, because neither can be certified and an uncertified process must
    /// never be stopped or updated over. What was wrong is where the skips landed: with nothing left,
    /// the answer came back <see cref="NotRunning"/>, so "I could not read what is there" and "nothing is
    /// there" were the same answer.
    ///
    /// That is safe for a caller asking MAY I STOP THIS - declining is the careful direction - and it is
    /// a fail-open for every caller asking IS THIS EMPTY, because there the same answer means PERMIT.
    /// <c>DirectorSupervisor.Start</c> reads it as permission to start a Director, so ONE corrupt
    /// registration file was enough to start a second Director on an instance home that already had a
    /// live one. Two Directors sharing one home is the shape of the failure that corrupted a database and
    /// took the hosted service down for thirty-two minutes on 30 July 2026.
    ///
    /// THE DISTINCTION ALREADY EXISTED ONE METHOD DOWN. <see cref="DirectorInstanceLocator.ReadSessionCount"/>
    /// returns null rather than a number when the roster cannot be read, and says why in its own words:
    /// the absence of that file beside a live Director is not zero sessions, it is no answer. The
    /// resolution path simply never got the same treatment.
    ///
    /// EVERY CALLER CHOOSES WHAT TO DO WITH IT, and they do not all choose the same thing: decline on it
    /// when deciding whether to STOP something, refuse on it when deciding whether something is EMPTY,
    /// and treat it as occupied when deciding whether to START something.
    /// <see cref="DirectorLookup.Unreadable"/> carries what was skipped and why, so a refusal can show
    /// its evidence rather than assert a cause.
    /// </summary>
    Unknown,
}

/// <summary>
/// One live Director, identified well enough to be acted on.
/// </summary>
/// <param name="DirectorId">Its identifier - the only string that names ONE Director process.</param>
/// <param name="Pid">Its process id, taken from the registration it wrote and verified against the
/// running process, not guessed from a scan.</param>
/// <param name="InstanceHome">The storage home it registered in, and therefore where its crash journal,
/// its updater state and its secret live.</param>
/// <param name="Version">The version it reported when it started. Written BY the running process, which
/// is what makes it usable as proof that a swapped build actually came up.</param>
/// <param name="ProcessStartedAtUtc">When the operating system started that process.</param>
/// <param name="ExecutablePath">
/// The image this process is running, or an empty string when it would not say. Used ONLY to tell an
/// INSTALL from a development build when more than one process claims one instance - never to identify a
/// Director. See <see cref="DirectorInstanceLocator.BreakTheTie"/> for why that distinction is not a
/// re-run of the defect this class exists to remove.
/// </param>
public sealed record SupervisedDirector(
    string DirectorId, int Pid, string InstanceHome, string Version, DateTime ProcessStartedAtUtc,
    string ExecutablePath)
{
    /// <summary>
    /// Whether this process was CERTIFIED as running the installed application's image.
    ///
    /// It exists because false has two causes that must not be allowed to look alike: the image was
    /// checked and did not match, or there was no installed path to check it against. Both mean the same
    /// thing where it counts - nothing may force-kill this process - and lumping them into a bare
    /// "resolved" is how a lone unidentified claimant came to authorize a kill in the first place. A
    /// question nobody could answer must never read as a yes.
    /// </summary>
    public bool IsInstalledImage { get; init; }
}

/// <summary>The answer to "which Director am I supervising", with the evidence behind it.</summary>
/// <param name="Outcome">What the search concluded.</param>
/// <param name="Director">The resolved Director when <paramref name="Outcome"/> is
/// <see cref="DirectorResolution.Running"/>; null otherwise.</param>
/// <param name="Candidates">Every live claimant, described. One entry when resolved, several when
/// ambiguous, none when nothing is running - so a log line can always say what was actually seen.</param>
/// <param name="Conflict">
/// Set when more than one live process claimed this instance - INCLUDING when the tie-break resolved it.
/// A resolved conflict is still a machine in a wrong state, and a tie-break that quietly does the right
/// thing is how the underlying defect survives unseen; this one survived long enough to be found by a
/// mission that was looking at something else. Callers must carry it somewhere a person will meet it, not
/// only into a log file.
/// </param>
/// <param name="Unreadable">
/// Everything that claimed this instance and could not be read or certified, each with the reason it was
/// skipped. Empty on a clean answer.
///
/// IT TRAVELS ON EVERY LOOKUP, NOT ONLY ON <see cref="DirectorResolution.Unknown"/>. A registration that
/// will not parse beside a Director that resolved perfectly is still a machine in a state that should not
/// exist, and it is exactly the state that becomes an Unknown the moment the good registration goes. A
/// caller carries it somewhere a person will meet it, in the same way <paramref name="Conflict"/> is
/// carried through a resolved tie-break rather than being dropped because the answer came out fine.
/// </param>
public sealed record DirectorLookup(
    DirectorResolution Outcome, SupervisedDirector? Director, IReadOnlyList<string> Candidates,
    string? Conflict = null, IReadOnlyList<string>? Unreadable = null)
{
    private readonly IReadOnlyList<string> _unreadable = Unreadable ?? Array.Empty<string>();

    /// <summary>
    /// What could not be read or certified. Never null, so a caller need not guard it.
    ///
    /// THE SETTER COALESCES, AND AN AUTO-PROPERTY INITIALIZER WOULD NOT HAVE. Written as
    /// <c>{ get; init; } = Unreadable ?? Array.Empty&lt;string&gt;();</c> the fallback runs only on the
    /// primary constructor, so <c>with { Unreadable = null }</c> would put a null straight through and
    /// the sentence above would be false at exactly the moment a caller relied on it. This was caught by
    /// reading the claim against the code under it rather than by any test - which is the point of doing
    /// that pass, because a promise written next to the code that breaks it reads as documentation.
    /// </summary>
    public IReadOnlyList<string> Unreadable
    {
        get => _unreadable;
        init => _unreadable = value ?? Array.Empty<string>();
    }
}

/// <summary>
/// Answers "is THIS Director running, and which process is it" - the question the launcher has to get
/// right before it stops anything or installs anything over it.
///
/// WHY THIS CLASS EXISTS. The launcher used to answer it with
/// <c>Process.GetProcessesByName("cc-director")</c>, keeping the first process whose image path matched
/// the installed executable. That is wrong on exactly the machine it matters on. A named instance is
/// the SAME executable with a different data home - <c>cc-director.exe</c> and
/// <c>cc-director.exe --instance work</c> have the same process name and the same image path - so a
/// machine running two of them produced two identical matches and the launcher kept whichever the
/// operating system listed first. That is not a wrong answer some of the time; it is an arbitrary
/// answer every time, and its consequences are a graceful shutdown sent to somebody else's Director and
/// a session count read from the wrong one, which is the number that decides whether an update may
/// interrupt live work.
///
/// HOW IT ANSWERS INSTEAD. Every Director writes a registration file into ITS OWN instance home naming
/// its identifier, its process id and the moment it registered. The launcher supervises exactly one
/// instance - the default one, because the launcher's DirectorSupervisor launches the installed
/// application with no instance flag - so it reads only that home's registrations and takes the process
/// id from the file. Neither the process name nor the image path is consulted at any point.
///
/// THE STALE-FILE PROBLEM, AND THE ONE CHECK THAT SOLVES IT. A registration outlives a Director that
/// was killed, and an operating system reuses process ids, so "the file says 34032 and 34032 exists" is
/// not proof. The check is the process's own START TIME: a Director always registers a moment AFTER it
/// starts, so the true owner's start time sits just before the registration's timestamp, while a
/// recycled id belongs to a process that started LATER - after the original died, which is necessarily
/// after the file was written. That single comparison rejects every recycled id and every registration
/// left by a process that is gone, using only fields the file already carries.
///
/// AMBIGUITY IS AN ANSWER WHEREVER IT IS REAL. When more than one live process claims the supervised
/// instance, this decides it ONLY when it is decidable and otherwise reports
/// <see cref="DirectorResolution.Ambiguous"/>, names every claimant, and callers refuse to act. The line
/// between the two is drawn in <see cref="BreakTheTie"/>, and that is the paragraph to read before
/// changing anything here: two processes running the SAME image are the real defect and stay refused,
/// because an arbitrary choice among equals is indistinguishable from a correct one right up until it
/// stops the wrong Director; a development build sitting in the installed application's instance home is
/// a different question with an answer, and refusing to update the real Director because somebody is
/// testing a slot build helps nobody.
///
/// The condition is real and not theoretical. It happens whenever a development build is started without
/// an instance flag, because the single-instance guard is keyed by executable slot rather than by
/// instance and does not prevent two different executables from claiming one home. It was found on the
/// machine this was written on, with two live Directors in one instance home. A RESOLVED CONFLICT IS
/// STILL A MACHINE IN A WRONG STATE and is reported as one, on <see cref="DirectorLookup.Conflict"/> as
/// well as in the log - a tie-break that silently does the right thing is how this defect stayed unseen.
/// </summary>
public sealed class DirectorInstanceLocator
{
    /// <summary>
    /// How long before its registration a process may have started and still be its author. Generous:
    /// a cold Director walks a splash screen, the engine and the control interface before it registers,
    /// and on a loaded machine that is seconds, not milliseconds. It only has to be shorter than the
    /// time between a process dying and its id being handed to something else, which is not seconds.
    /// </summary>
    public static readonly TimeSpan RegistrationLag = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How far AFTER its registration a process may have started and still be its author: essentially
    /// not at all. The margin exists only because the two timestamps are taken from different sources
    /// on the same clock, not because a real Director ever registers before it starts.
    /// </summary>
    public static readonly TimeSpan RegistrationSkew = TimeSpan.FromSeconds(2);

    private readonly string _instanceHome;
    private readonly string? _legacyFlatDirectory;
    private readonly string _installedDirectorPath;

    /// <param name="instanceHome">The storage home of the instance to resolve. Tests aim this at a
    /// throwaway directory; production uses the default constructor.</param>
    /// <param name="legacyFlatDirectory">
    /// The pre-1.8 registration directory at the storage ROOT, or null to ignore it.
    ///
    /// It is read as PART OF the supervised instance rather than as a second place to look, because
    /// that is what it is: before 1.8 the installed Director had no instance folder and registered
    /// here, and from 1.8 the same Director registers one level in as instance "default". Two layouts,
    /// one Director. Dropping it would have quietly stopped a machine that has not been through an
    /// upgrade from being supervised at all, and named instances are still excluded either way - which
    /// is the precision this class exists for.
    /// </param>
    /// <param name="installedDirectorPath">
    /// The installed application's image. Used ONLY by the tie-break, and only to tell an install from a
    /// development build when more than one process claims this instance - never to identify a Director.
    /// Empty means no tie-break is possible, so every conflict is refused.
    /// </param>
    public DirectorInstanceLocator(string instanceHome, string? legacyFlatDirectory = null,
        string? installedDirectorPath = null)
    {
        _instanceHome = instanceHome ?? throw new ArgumentNullException(nameof(instanceHome));
        _legacyFlatDirectory = legacyFlatDirectory;
        _installedDirectorPath = installedDirectorPath ?? "";
    }

    /// <summary>The instance home being resolved.</summary>
    public string InstanceHome => _instanceHome;

    /// <summary>
    /// How the image of a live process is read. Production always uses <see cref="ExecutablePathOf"/>.
    ///
    /// It is a seam ONLY so the refusal below can be tested. "A claimant that will not say what it is
    /// running must be refused" is a guard that FAILS OPEN if it is wrong - it would silently let an
    /// unidentifiable process be treated as not-the-install and hand the tie-break to somebody else - and
    /// the one thing worse than that guard being absent is it being present and never exercised. The
    /// unreadable case cannot be produced honestly in a unit test (it needs a process this one is not
    /// allowed to interrogate), so what the test proves is the BRANCH, not that Windows really refuses
    /// for an elevated process. Stated rather than implied.
    /// </summary>
    internal Func<Process, string> ReadExecutablePath { get; set; } = ExecutablePathOf;

    /// <summary>Every directory the supervised Director could have registered in, current layout first.</summary>
    public IEnumerable<string> RegistrationDirectories
    {
        get
        {
            yield return Path.Combine(_instanceHome, "config", "director", "instances");
            if (_legacyFlatDirectory is { Length: > 0 } flat) yield return flat;
        }
    }

    /// <summary>
    /// Resolve the supervised Director. Never throws: a launcher that throws while working out whether to
    /// stop something is worse than one that declines to.
    ///
    /// A SKIP IS RECORDED, NEVER SWALLOWED (issue #2730). Two things here cannot be certified and are
    /// therefore not acted on: a registration that will not parse, and a live process that will not say
    /// when it started. Declining to act on them is right. What was wrong is that they then vanished, so
    /// with nothing left the answer was <see cref="DirectorResolution.NotRunning"/> - the same answer as
    /// a genuinely empty instance home. They are now collected, and an answer built entirely out of skips
    /// is <see cref="DirectorResolution.Unknown"/>, which no caller may read as permission.
    /// </summary>
    public DirectorLookup Resolve()
    {
        var live = new List<SupervisedDirector>();
        var described = new List<string>();
        var unreadable = new List<string>();

        foreach (var (file, home, dto, unreadableReason) in ReadRegistrations())
        {
            if (dto is null)
            {
                // Something IS in this directory and it could not be read. Recorded rather than dropped:
                // see the class comment on DirectorResolution.Unknown.
                unreadable.Add(unreadableReason ?? $"the registration {file} could not be read");
                continue;
            }

            var (process, uninspectable) = TryGetLiveProcess(dto.Pid);
            if (uninspectable is not null)
            {
                // NOT THE SAME AS A DEAD PROCESS. The operating system refused to tell us about this
                // process id - it may be a live Director we are not allowed to look at. Erasing the
                // claim here was the same fail-open, one method below the one it was written for.
                FileLog.Write($"[DirectorInstanceLocator] {uninspectable}");
                unreadable.Add(uninspectable);
                continue;
            }
            if (process is null) continue;   // positively dead: no such process id

            using (process)
            {
                DateTime startedAt;
                try
                {
                    startedAt = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex)
                {
                    // A process that cannot be interrogated cannot be certified as the registration's
                    // author, and an uncertified process must not be stopped or updated over. It is ALIVE
                    // though, which is why this is recorded as an unreadable claim rather than dropped:
                    // a live process holding this instance home is the strongest possible reason not to
                    // start a second Director in it.
                    var reason = $"pid={dto.Pid} from {file} is alive and could not be asked when it started "
                                 + $"({ex.Message}), so it cannot be certified as the Director that wrote that "
                                 + "registration";
                    FileLog.Write($"[DirectorInstanceLocator] {reason}. Not acted on, and NOT treated as absent.");
                    unreadable.Add(reason);
                    continue;
                }

                var earliest = dto.StartedAt - RegistrationLag;
                var latest = dto.StartedAt + RegistrationSkew;
                if (startedAt < earliest || startedAt > latest)
                {
                    FileLog.Write($"[DirectorInstanceLocator] pid={dto.Pid} is alive but started {startedAt:o}, "
                                  + $"outside the window {earliest:o}..{latest:o} implied by the registration in "
                                  + $"{file} (written {dto.StartedAt:o}). This is a process that INHERITED the id of "
                                  + "a dead Director, or a registration left behind by one. Ignoring it.");
                    continue;
                }

                var executable = ReadExecutablePath(process);
                live.Add(new SupervisedDirector(dto.DirectorId, dto.Pid, home, dto.Version, startedAt, executable));
                described.Add($"directorId={dto.DirectorId} pid={dto.Pid} version={dto.Version} "
                              + $"started={startedAt:o} exe={(executable.Length == 0 ? "UNREADABLE" : executable)} "
                              + $"registration={file}");
            }
        }

        if (live.Count == 0)
        {
            // NOTHING SURVIVED. Which of the two answers that is depends entirely on whether anything was
            // skipped on the way here, and the whole of issue #2730 is that this used to be one answer.
            if (unreadable.Count > 0)
            {
                var reason = $"whether a Director holds {_instanceHome} is UNKNOWN: nothing could be certified, "
                             + $"and {unreadable.Count} claim(s) could not be read - {string.Join("; ", unreadable)}. "
                             + "This is NOT an empty instance home. Do not start a Director here, and do not read "
                             + "it as idle.";
                FileLog.Write($"[DirectorInstanceLocator] {reason}");
                return new DirectorLookup(DirectorResolution.Unknown, null, described, reason, unreadable);
            }

            return new DirectorLookup(DirectorResolution.NotRunning, null, described, null, unreadable);
        }

        if (live.Count == 1)
            return ResolveSingleClaimant(live[0], described) with { Unreadable = unreadable };

        return BreakTheTie(live, described) with { Unreadable = unreadable };
    }

    /// <summary>
    /// One live claimant. It still has to be the process this launcher supervises.
    ///
    /// WHY A LONE CLAIMANT IS NOT AUTOMATICALLY THE ANSWER - and this reverses what this class used to
    /// do. Being alone was treated as identification: a registration naming a live pid whose start time
    /// fits the window was resolved without the image ever being consulted, and
    /// <see cref="Launcher.DirectorSupervisor"/> will FORCE-KILL what this returns. So a registration
    /// naming a process that is not a Director at all - a stale file whose pid was reused by something
    /// with a compatible start time, or one written by a development build - authorized killing that
    /// process. Independent inspection found it, and found a test standing over it holding it in place
    /// by name.
    ///
    /// THE RULE THIS CLASS ENFORCES IS UNCHANGED, AND THIS IS THE PART TO READ BEFORE CONCLUDING
    /// OTHERWISE. A Director is still never IDENTIFIED by its image path: WHICH Director this is comes
    /// from the registration, as it always did, and the image cannot distinguish two named instances of
    /// one install because they are one image. That is why the two-claimant case below is still refused.
    /// What the image answers is a different question - may this launcher END this process - and the
    /// answer has to be yes before anything is stopped or updated over. The tie-break has consulted the
    /// image for exactly this reason since it was written; the lone claimant was the path that never did.
    ///
    /// WHEN THERE IS NOTHING TO COMPARE AGAINST. A locator built with no installed path (the desktop
    /// application's own "is something running there" check does this) cannot judge the image, so it
    /// resolves as before - but the claimant is marked as not certified, and
    /// <see cref="Launcher.DirectorSupervisor"/> refuses to force-kill an uncertified process. An
    /// unanswerable question must not read as a yes.
    /// </summary>
    private DirectorLookup ResolveSingleClaimant(SupervisedDirector claimant, List<string> described)
    {
        if (_installedDirectorPath.Length == 0)
        {
            // Nothing to compare against. Resolved, explicitly UNCERTIFIED - see the caller-side refusal.
            return new DirectorLookup(DirectorResolution.Running, claimant with { IsInstalledImage = false },
                described);
        }

        if (!IsInstalledDirector(claimant.ExecutablePath))
        {
            var reason = $"the only live process claiming {_instanceHome} is not the Director this launcher "
                         + $"supervises: pid={claimant.Pid} runs "
                         + (claimant.ExecutablePath.Length == 0 ? "an image it would not name" : claimant.ExecutablePath)
                         + $", and the installed application is {_installedDirectorPath}. It is a development build, "
                         + "or a registration whose process id was reused by something else. Nothing will be "
                         + "stopped, restarted or updated over it - it is not this launcher's process to end.";
            FileLog.Write($"[DirectorInstanceLocator] REFUSING to act: {reason}");
            return new DirectorLookup(DirectorResolution.NotSupervised, null, described, reason);
        }

        return new DirectorLookup(DirectorResolution.Running, claimant with { IsInstalledImage = true }, described);
    }

    /// <summary>
    /// More than one live process claims this instance. Decide it when it is decidable, refuse when it is
    /// not.
    ///
    /// WHY AN EXECUTABLE PATH IS ADMISSIBLE HERE AND INADMISSIBLE ABOVE - READ THIS BEFORE CONCLUDING THE
    /// DEFECT WAS REINTRODUCED. The rule this class enforces is that a Director is never IDENTIFIED by its
    /// image path, and that rule is untouched: <see cref="Resolve"/> never consults the path, and a single
    /// claimant is resolved without looking at it. The reason is that every named instance of one install
    /// runs the SAME executable - <c>cc-director.exe</c> and <c>cc-director.exe --instance work</c> are
    /// one image - so a path cannot tell two instances apart. That case is the defect, and it is still
    /// REFUSED below.
    ///
    /// What a path CAN tell apart is an INSTALL from a development build, because those are different
    /// images in different places. That is a different question from "which Director is this", and it has
    /// an answer the launcher is entitled to act on: it supervises the installed application, a slot build
    /// somebody is testing is not the machine's Director of record, and refusing to update or stop the
    /// real one because a test build sits in the same instance home helps nobody. So the tie-break narrows
    /// the refusal to the case that is genuinely undecidable rather than refusing on every conflict.
    ///
    /// THIS IS NOT A FALLBACK. That rule forbids two PATHS to one capability - try one thing, fall back to
    /// another - because the second path is a door the mission exists to close. This is one path with a
    /// tie-break on an ambiguous input: no second mechanism, nothing retried, and the undecidable case
    /// still ends in a refusal rather than a guess.
    ///
    /// A CLAIMANT WHOSE IMAGE CANNOT BE READ POISONS THE TIE-BREAK. An elevated process will not say what
    /// it is running, and "I could not check" is not "it is not the install" - treating it as the latter
    /// would let the guard fail open in exactly the case where something unusual is going on. Unknown
    /// means refuse.
    /// </summary>
    private DirectorLookup BreakTheTie(List<SupervisedDirector> live, List<string> described)
    {
        var conflict = $"{live.Count} live processes claim the instance at {_instanceHome}: "
                       + string.Join(" | ", described);

        if (live.Any(d => d.ExecutablePath.Length == 0))
        {
            FileLog.Write($"[DirectorInstanceLocator] REFUSING to choose: {conflict}. At least one claimant would "
                          + "not say what image it is running, so it cannot be ruled out as a second copy of the "
                          + "installed Director. Nothing will be stopped, restarted or updated until this is fixed.");
            return new DirectorLookup(DirectorResolution.Ambiguous, null, described, conflict);
        }

        var installed = live.Where(d => IsInstalledDirector(d.ExecutablePath)).ToList();
        if (installed.Count != 1)
        {
            FileLog.Write($"[DirectorInstanceLocator] REFUSING to choose: {conflict}. "
                          + (installed.Count == 0
                              ? "None of them is the installed Director, so there is nothing here this launcher "
                                + "supervises."
                              : $"{installed.Count} of them run the SAME installed executable, which is the case a "
                                + "path cannot decide - two named instances of one install look identical. This is "
                                + "the defect this launcher refuses to guess about.")
                          + " Nothing will be stopped, restarted or updated until this is fixed.");
            return new DirectorLookup(DirectorResolution.Ambiguous, null, described, conflict);
        }

        // Decided - and the machine is STILL WRONG. The whole reason this defect went unnoticed is that
        // everything carried on working, so it is said at every pass and handed to the caller to put
        // somewhere other than this log.
        FileLog.Write($"[DirectorInstanceLocator] CONFLICT - {conflict}. Resolved to the INSTALLED Director "
                      + $"(directorId={installed[0].DirectorId} pid={installed[0].Pid} exe={installed[0].ExecutablePath}) "
                      + "because the others are not the installed application. THIS MACHINE IS STILL IN A WRONG "
                      + "STATE: two processes should never share one instance home, and the single-instance guard "
                      + "does not prevent it because it is keyed by executable slot rather than by instance.");
        return new DirectorLookup(DirectorResolution.Running, installed[0] with { IsInstalledImage = true },
            described, conflict);
    }

    /// <summary>True when this image is the installed Director (inside the bundle on macOS).</summary>
    private bool IsInstalledDirector(string executablePath)
    {
        if (executablePath.Length == 0 || _installedDirectorPath.Length == 0) return false;
        return OperatingSystem.IsWindows()
            ? string.Equals(executablePath, _installedDirectorPath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(executablePath, _installedDirectorPath, StringComparison.Ordinal)
              || executablePath.StartsWith(_installedDirectorPath + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The image a process is running, or "" when it will not say. Windows reads the main module; macOS
    /// and Linux ask /bin/ps, because MainModule is unreliable there.
    /// </summary>
    private static string ExecutablePathOf(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return process.MainModule?.FileName ?? "";
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorInstanceLocator] pid={process.Id} would not say what image it is "
                              + $"running: {ex.Message}");
                return "";
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("comm=");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(process.Id.ToString());

            using var ps = Process.Start(psi);
            if (ps is null) return "";
            var output = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            return ps.ExitCode == 0 ? output : "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorInstanceLocator] pid={process.Id} image path could not be read: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// How many sessions the resolved Director is holding, read from the roster it maintains on disk.
    ///
    /// THIS REPLACES ASKING IT OVER A SOCKET, AND IT IS NOT A WEAKER ANSWER. The objection to a file is
    /// that it says what was true at some earlier moment - but this file is rewritten on every change to
    /// the session set, atomically, and it is DELETED on a clean shutdown. So while the Director is
    /// alive the file is exactly as current as the roster itself, and its absence beside a live Director
    /// is not "zero sessions", it is "no answer" - which is why this returns null there rather than a
    /// number that would read as idle and let an update proceed.
    /// </summary>
    public int? ReadSessionCount(SupervisedDirector director)
    {
        ArgumentNullException.ThrowIfNull(director);
        var directory = Path.Combine(director.InstanceHome, "config", "director", "crash-journal");
        var roster = DirectorCrashJournal.ReadLiveRoster(director.DirectorId, directory);
        if (roster is null)
        {
            FileLog.Write($"[DirectorInstanceLocator] the Director {director.DirectorId} is running but no live "
                          + $"session roster was readable under {directory}, so how busy it is is UNKNOWN. It is not "
                          + "reported as idle: an update must never proceed on a missing answer.");
            return null;
        }

        return roster.Sessions.Count;
    }

    /// <summary>
    /// Every registration file found, parsed where possible.
    ///
    /// A FILE THAT WOULD NOT PARSE IS YIELDED WITH A NULL DTO AND A REASON, never dropped (issue #2730).
    /// It used to be skipped here, silently, and the caller then had no way to tell an empty directory
    /// from a directory full of files it could not read - which is how a corrupt registration came to
    /// authorize starting a second Director on a live instance home.
    /// </summary>
    private IEnumerable<(string File, string Home, Gateway.Contracts.DirectorDto? Dto, string? Unreadable)> ReadRegistrations()
    {
        foreach (var dir in RegistrationDirectories)
        {
            // The storage home this registration belongs to: three levels up from
            // <home>/config/director/instances. Carried per registration rather than assumed, because
            // the crash journal that says how busy that Director is lives under ITS home, and the two
            // layouts put it in different places. Resolved BEFORE the listing, because an unreadable
            // directory has to be reported against its home too.
            var home = Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));

            // ASKED BY TRYING, NOT BY Directory.Exists. That check answers FALSE for a directory that
            // exists and cannot be reached, so "not there" and "not readable" came back as one answer -
            // the very fold this change exists to remove, one layer above the one it was written for.
            // GetFiles discriminates them positively: a directory that is genuinely absent throws
            // DirectoryNotFoundException, and anything else means something IS there and could not be
            // listed.
            string[]? files = null;
            string? listingFailure = null;
            var absent = false;
            try
            {
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (DirectoryNotFoundException)
            {
                // Genuinely absent, and that is ordinary: the pre-1.8 flat path does not exist on a
                // modern install, and an instance home has no registrations directory until a Director
                // has run there. Nothing is there, so nothing is claimed.
                absent = true;
            }
            catch (Exception ex)
            {
                listingFailure = $"the registration directory {dir} exists and could not be listed: {ex.Message}";
            }

            if (absent) continue;

            if (listingFailure is not null)
            {
                // It exists and could not be read. A directory of registrations we cannot see is the
                // strongest possible reason NOT to conclude the instance home is empty. Yielded out here
                // rather than from the catch, which the language forbids in an iterator.
                FileLog.Write($"[DirectorInstanceLocator] {listingFailure}");
                yield return (dir, home, null, listingFailure);
                continue;
            }

            foreach (var file in files!)
            {
                Gateway.Contracts.DirectorDto? dto = null;
                string? failure = null;
                try
                {
                    dto = JsonSerializer.Deserialize<Gateway.Contracts.DirectorDto>(File.ReadAllText(file), JsonOptions);
                }
                catch (Exception ex)
                {
                    failure = $"the registration {file} could not be read: {ex.Message}";
                }

                if (failure is not null)
                {
                    FileLog.Write($"[DirectorInstanceLocator] {failure}");
                    yield return (file, home, null, failure);
                    continue;
                }

                // Parsed, and it does not name a Director. A file whose contents are valid JavaScript
                // Object Notation and are not a registration is the SAME class of fault as one that
                // would not parse at all - in both cases a file is sitting in this directory and this
                // code cannot tell what process, if any, it stands for. Reading it as an empty directory
                // is the failure being fixed, and it does not become safe because the parser succeeded.
                // StartedAt IS PART OF BEING COMPLETE, and leaving it out of this check made the
                // "never throws" contract on Resolve false: an unset stamp is DateTime.MinValue, and
                // subtracting RegistrationLag from it throws ArgumentOutOfRangeException outside every
                // catch in this file. It is also right on the merits - the stamp is the ONLY thing that
                // tells this Director from a process that inherited its id, so a registration without
                // one certifies nothing and belongs with the others that could not be read.
                if (dto is null || dto.Pid <= 0 || string.IsNullOrWhiteSpace(dto.DirectorId)
                    || dto.StartedAt == default || dto.StartedAt - RegistrationLag < DateTime.MinValue)
                {
                    var incomplete = $"the registration {file} parsed but names no usable Director "
                                     + $"(directorId={(string.IsNullOrWhiteSpace(dto?.DirectorId) ? "missing" : dto!.DirectorId)}, "
                                     + $"pid={dto?.Pid ?? 0}, "
                                     + $"startedAt={(dto is null || dto.StartedAt == default ? "missing" : dto.StartedAt.ToString("o"))})";
                    FileLog.Write($"[DirectorInstanceLocator] {incomplete}");
                    yield return (file, home, null, incomplete);
                    continue;
                }

                yield return (file, home, dto, null);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The live process for a registered process id.
    ///
    /// THREE ANSWERS, NEVER TWO (issue #2730). A null process with a null reason means the process id is
    /// POSITIVELY DEAD - the operating system says no such process, which is a fact. A non-null reason
    /// means the operating system REFUSED TO SAY, which is not a fact about the process and must never
    /// be treated as one: it may be a live Director this launcher is not permitted to inspect. Collapsing
    /// the second into the first is what let an inaccessible live Director read as an empty instance
    /// home, one method below the defect this change was written for.
    ///
    /// THIS DISCIPLINE IS NOT NEW HERE - IT IS COPIED FROM ITS SIBLING, WHICH HAD IT ALL ALONG.
    /// <c>DirectorRegistry.IsProcessDead</c> in the Gateway asks the identical question about the
    /// identical thing, catches <see cref="ArgumentException"/> ALONE as proof of death, and returns
    /// do-not-assume-dead for everything else - with the reason written on it: "A permission or other
    /// unexpected error returns false (do not assume dead) so we never delete on uncertainty." One
    /// assembly away, the same question was already being answered correctly; this one folded every
    /// exception into the not-running answer, and that is what made the defect dangerous.
    ///
    /// The two now agree, and the citation is here so the next reader can check that rather than take it.
    /// Worth remembering as a habit: in this repository the correct answer to a question is usually
    /// already written somewhere and simply not reached, so the question to ask is not "what is missing"
    /// but "where else does this repository already answer exactly this".
    /// </summary>
    private static (Process? Process, string? Uninspectable) TryGetLiveProcess(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return (null, null);      // it ran and is gone: a fact
            }
            return (process, null);
        }
        catch (ArgumentException)
        {
            return (null, null);          // no such process: also a fact
        }
        catch (Exception ex)
        {
            return (null, $"pid={pid} could not be inspected ({ex.Message}), so whether it is a live "
                          + "Director is unknown - it is NOT being treated as absent");
        }
    }
}
