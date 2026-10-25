using System.Diagnostics;
using CcDirector.Core.Instances;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The launcher has to answer "is THIS Director running, and which process is it" before it stops
/// anything or installs anything over it. These pin that answer.
///
/// THE DEFECT THEY PIN. The launcher used to scan by process name and keep the first process whose
/// image path matched the installed executable. A named instance is the SAME executable with a
/// different data home - <c>cc-director.exe</c> and <c>cc-director.exe --instance work</c> have the
/// same name and the same image path - so a machine running two of them produced two identical matches
/// and the launcher kept whichever the operating system happened to list first. That is not a wrong
/// answer some of the time; it is an arbitrary answer every time, and its consequences are a shutdown
/// sent to somebody else's Director and a session count read from the wrong one - the number that
/// decides whether an update may interrupt live work.
///
/// The old tests could not have caught it. They asserted that BOTH the flat root and every named
/// instance home were scanned, which is exactly the behaviour that made two instances indistinguishable.
/// The scan was correct as a way to find registrations and wrong as a way to identify ONE Director.
///
/// THE OTHER DEFECT THEY PIN. A registration outlives a Director that was killed, and an operating
/// system reuses process ids, so "the file says 34032 and 34032 exists" is not proof. The identity
/// check is the process start time against the registration's timestamp - <see
/// cref="ProcessStartTime_AfterTheRegistration_IsRejectedAsARecycledProcessId"/> is the one that
/// fails without it.
///
/// These drive the production <see cref="DirectorInstanceLocator"/>, not a copy of it.
/// </summary>
[Collection(StorageRootCollection.Name)]
public sealed class DirectorInstanceLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly string? _previousInstancesDir;

    public DirectorInstanceLocatorTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _previousInstancesDir = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", null);

        _root = Path.Combine(Path.GetTempPath(), "cc-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", _previousInstancesDir);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temporary directory that outlives the run is not a failure */ }
    }

    private string InstanceHome(string slug) => Path.Combine(_root, "instances", slug);
    private string FlatDirectory => Path.Combine(_root, "config", "director", "instances");
    private string InstanceDirectory(string slug) =>
        Path.Combine(InstanceHome(slug), "config", "director", "instances");

    /// <summary>The locator as production builds it: the default instance, plus the pre-1.8 flat path.</summary>
    /// <param name="installedDirectorPath">
    /// What counts as the installed application for the tie-break. Defaulting it to THIS TEST PROCESS's
    /// own executable is what makes the tie-break testable at all: every claimant these tests write is
    /// this process, so "the installed one" and "not the installed one" are both expressible by pointing
    /// this somewhere else.
    /// </param>
    private DirectorInstanceLocator Locator(string? installedDirectorPath = null) =>
        new(InstanceHome("default"), FlatDirectory,
            installedDirectorPath ?? Environment.ProcessPath ?? "");

    /// <summary>
    /// Write a registration exactly as a Director writes one.
    ///
    /// The pid defaults to this test process, so the entry describes something genuinely alive, and
    /// StartedAt defaults to a moment AFTER this process started - which is the truthful shape, because
    /// a Director always registers after it starts. A test that wants to exercise the identity check
    /// passes a StartedAt that breaks that ordering.
    /// </summary>
    private static void WriteRegistration(
        string directory, string directorId, int port = 7879, int? pid = null,
        DateTime? startedAt = null, string version = "1.9.7")
    {
        Directory.CreateDirectory(directory);
        var stamp = startedAt ?? DateTime.UtcNow;
        var json = $$"""
        {
          "DirectorId": "{{directorId}}",
          "Pid": {{pid ?? Environment.ProcessId}},
          "StartedAt": "{{stamp:o}}",
          "ControlEndpoint": "http://127.0.0.1:{{port}}",
          "Version": "{{version}}"
        }
        """;
        File.WriteAllText(Path.Combine(directory, directorId + ".json"), json);
    }

    private static void WriteRoster(string home, string directorId, int sessions)
    {
        var dir = Path.Combine(home, "config", "director", "crash-journal");
        Directory.CreateDirectory(dir);
        var entries = string.Join(",", Enumerable.Range(0, sessions).Select(i =>
            $$"""{"SessionId":"session-{{i}}","RepoPath":"D:/repo","Agent":"ClaudeCode"}"""));
        File.WriteAllText(Path.Combine(dir, directorId + ".json"),
            $$"""{"DirectorId":"{{directorId}}","Pid":{{Environment.ProcessId}},"Sessions":[{{entries}}]}""");
    }

    [Fact]
    public void NothingRegistered_IsNotRunning()
    {
        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.NotRunning, lookup.Outcome);
        Assert.Null(lookup.Director);
    }

    [Fact]
    public void ALiveRegistrationInTheDefaultInstance_ResolvesToThatDirector()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000001", version: "1.9.7");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("aaaa0001-0000-0000-0000-000000000001", lookup.Director!.DirectorId);
        Assert.Equal(Environment.ProcessId, lookup.Director.Pid);
        Assert.Equal("1.9.7", lookup.Director.Version);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. A second Director running as a NAMED instance is the same executable with
    /// the same process name, so the old scan matched it just as well as the installed default and
    /// could return either. The launcher supervises the default instance, and only that one may be
    /// resolved.
    /// </summary>
    [Fact]
    public void ANamedInstanceRunningAlongside_IsNotTheSupervisedDirector()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-00000000000d");
        WriteRegistration(InstanceDirectory("work"), "aaaa0001-0000-0000-0000-00000000000e",
            port: 7881);

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("aaaa0001-0000-0000-0000-00000000000d", lookup.Director!.DirectorId);
        Assert.Single(lookup.Candidates);
    }

    /// <summary>
    /// TWO CLAIMANTS RUNNING THE SAME IMAGE IS THE REAL DEFECT AND STAYS REFUSED. This is the case an
    /// executable path cannot decide - two named instances of one install are one image - and it is
    /// exactly what the old launcher guessed at. Both claimants here are this test process, so they
    /// share an executable by construction.
    /// </summary>
    [Fact]
    public void TwoClaimantsRunningTheSameExecutable_IsAmbiguousAndNamesBoth()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000010");
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000011", port: 7880);

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Ambiguous, lookup.Outcome);
        Assert.Null(lookup.Director);
        Assert.Equal(2, lookup.Candidates.Count);
        Assert.NotNull(lookup.Conflict);
    }

    /// <summary>
    /// The tie-break PREFERS the install; it does not invent one. When no claimant is the installed
    /// application there is nothing here this launcher supervises, and it must still refuse.
    /// </summary>
    [Fact]
    public void WhenNoClaimantIsTheInstalledDirector_ItStillRefuses()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000012");
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000013", port: 7880);

        var lookup = Locator(installedDirectorPath: NotThisProcessPath).Resolve();

        Assert.Equal(DirectorResolution.Ambiguous, lookup.Outcome);
        Assert.NotNull(lookup.Conflict);
    }

    /// <summary>
    /// A LONE CLAIMANT THAT IS NOT THE INSTALLED APPLICATION IS REFUSED - and this test replaces one that
    /// asserted the OPPOSITE, by name, and held the defect in place.
    ///
    /// The old test was called "a single claimant is resolved even when it is not the installed Director",
    /// and its stated reasoning was sound as far as it went: the image must never become the way a
    /// Director is IDENTIFIED, because two named instances of one install share an image. But identity is
    /// not the question the kill path asks. DirectorSupervisor.StopAsync will force-kill what the locator
    /// returns, so resolving a lone unexamined claimant authorized ending a process that might be a
    /// development build, or something that merely inherited a dead Director's process id inside the
    /// start-time window. Independent inspection found the acceptance and the kill path, and found this
    /// test standing over them.
    ///
    /// Identity still comes from the registration. What changed is that acting on it now also requires
    /// the image to be the one this launcher supervises.
    /// </summary>
    [Fact]
    public void ASingleClaimantThatIsNotTheInstalledDirector_IsRefused()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000014");

        var lookup = Locator(installedDirectorPath: NotThisProcessPath).Resolve();

        Assert.Equal(DirectorResolution.NotSupervised, lookup.Outcome);
        Assert.Null(lookup.Director);
        Assert.NotNull(lookup.Conflict);
        Assert.Contains("not the Director this launcher supervises", lookup.Conflict);

        // NotSupervised is NOT NotRunning: something is holding the instance home, so nothing new may be
        // started on top of it. Saying "not running" here would have the launcher start a second one.
        Assert.Single(lookup.Candidates);
    }

    /// <summary>
    /// The lone claimant that IS the installed application resolves, and is marked certified - which is
    /// what the kill path requires before it will force-kill anything.
    /// </summary>
    [Fact]
    public void ASingleClaimantRunningTheInstalledImage_ResolvesAndIsCertified()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-00000000001a");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.True(lookup.Director!.IsInstalledImage);
        Assert.Null(lookup.Conflict);
    }

    /// <summary>
    /// NO INSTALLED PATH MEANS NOT CERTIFIED, NOT "PROBABLY FINE". The desktop application builds a
    /// locator with nothing to compare against, because all it asks is whether something is running
    /// there. It still resolves - but the claimant carries IsInstalledImage false, and that is what stops
    /// the launcher's kill path treating an unanswerable question as a yes.
    /// </summary>
    [Fact]
    public void WithNoInstalledPathToCompareAgainst_TheClaimantResolvesButIsNotCertified()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-00000000001b");

        var lookup = new DirectorInstanceLocator(InstanceHome("default"), FlatDirectory).Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.False(lookup.Director!.IsInstalledImage);
    }

    /// <summary>
    /// THE APPROVED TIE-BREAK, AND THE ARCHITECT'S CONDITION ON IT. A development build sitting in the
    /// installed application's instance home is a different question from "which of two instances is
    /// this", and it has an answer: the launcher supervises the install. So this resolves - AND the
    /// conflict still travels on the answer, because a resolved conflict is still a machine in a wrong
    /// state and a tie-break that silently does the right thing is how the defect stays unseen.
    /// </summary>
    [Fact]
    public void TwoClaimantsRunningDIFFERENTImages_ResolveToTheInstalledOne_AndStillReportTheConflict()
    {
        var locator = Locator(installedDirectorPath: InstalledImage);
        ClaimantRunning(locator, InstalledImage, developmentBuild: DevelopmentBuildImage);

        // This process stands in for the development build; the second claimant for the install.
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000015");
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000016",
            port: 7880, pid: SecondClaimantPid);

        var lookup = locator.Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal(SecondClaimantPid, lookup.Director!.Pid);
        Assert.Equal(InstalledImage, lookup.Director.ExecutablePath);
        Assert.NotNull(lookup.Conflict);
        Assert.Contains("claim the instance", lookup.Conflict);
    }

    /// <summary>
    /// UNKNOWN MEANS REFUSE. A claimant that will not say what image it is running cannot be ruled out as
    /// a second copy of the install, and "I could not check" is not "it is not the install" - treating it
    /// as the latter would let this guard FAIL OPEN in exactly the case where something unusual is going
    /// on. Without this the guard would have no test at all: injecting its removal changes nothing that
    /// any other test can see.
    ///
    /// WHAT THIS DOES NOT PROVE, stated rather than implied: it exercises the BRANCH through a seam, not
    /// that Windows really refuses to report the image of an elevated process. That case cannot be
    /// produced honestly from a unit test.
    /// </summary>
    [Fact]
    public void AClaimantWhoseImageCannotBeRead_ForcesARefusal()
    {
        var locator = Locator(installedDirectorPath: InstalledImage);
        // Everything answers as usual EXCEPT this process, which will not say what it is running. Without
        // the guard the remaining claimant is the install and the tie-break would resolve happily.
        ClaimantRunning(locator, InstalledImage, developmentBuild: "");

        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000017");
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000018",
            port: 7880, pid: SecondClaimantPid);

        var lookup = locator.Resolve();

        Assert.Equal(DirectorResolution.Ambiguous, lookup.Outcome);
        Assert.Null(lookup.Director);
        Assert.NotNull(lookup.Conflict);
    }

    /// <summary>An image this test process is definitely not running.</summary>
    private static string NotThisProcessPath =>
        Path.Combine(Path.GetTempPath(), "definitely-not-the-installed-director.exe");

    /// <summary>The image the tie-break tests call the installed application.</summary>
    private static string InstalledImage => Path.Combine(Path.GetTempPath(), "installed", "cc-director");

    /// <summary>The image the tie-break tests call a development build.</summary>
    private static string DevelopmentBuildImage => Path.Combine(Path.GetTempPath(), "slot-build", "cc-director");

    /// <summary>A process id no registration but the second claimant's names.</summary>
    private const int SecondClaimantPid = int.MaxValue - 15;

    /// <summary>
    /// Make <see cref="SecondClaimantPid"/> a live claimant running <paramref name="image"/>, and this
    /// process a claimant running <paramref name="developmentBuild"/> ("" for one that will not say).
    ///
    /// Through the locator's seams rather than a second real process: what these tests pin is the
    /// TIE-BREAK, and a helper process made them wait on the machine - it had to be polled until the
    /// operating system reported its image, and on Linux /bin/sh is a link to another shell, so the image
    /// read back was never the one the test had named. Reading a real process's image is pinned by the
    /// single-claimant tests above, which run against this process. The second claimant is this process
    /// again under another id, so its start time is a real one that fits its registration.
    /// </summary>
    private static void ClaimantRunning(DirectorInstanceLocator locator, string image, string developmentBuild)
    {
        var second = Process.GetCurrentProcess();
        var inspect = locator.InspectProcess;
        locator.InspectProcess = pid => pid == SecondClaimantPid ? (second, null) : inspect(pid);
        locator.ReadExecutablePath = process => ReferenceEquals(process, second) ? image : developmentBuild;
    }

    /// <summary>
    /// An update replaces the file a running Director was started from, and Linux then reports that
    /// process's image as the old path with " (deleted)" appended. It is still the installed Director -
    /// the one the update has to stop - so the suffix must not make it read as something else.
    /// </summary>
    [Theory]
    [InlineData("/opt/cc-director/app/cc-director", "/opt/cc-director/app/cc-director")]
    [InlineData("/opt/cc-director/app/cc-director (deleted)", "/opt/cc-director/app/cc-director")]
    [InlineData("/opt/cc-director/app/cc-director (deleted) (deleted)", "/opt/cc-director/app/cc-director (deleted)")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LinuxImageFromProcLink_RemovesOnlyTheKernelsReplacedFileSuffix(string? link, string expected)
    {
        Assert.Equal(expected, DirectorInstanceLocator.LinuxImageFromProcLink(link));
    }

    /// <summary>
    /// A registration whose process id is long gone is not evidence of anything, and this is the exact
    /// shape a force-killed Director leaves behind.
    /// </summary>
    [Fact]
    public void ARegistrationForADeadProcessId_IsIgnored()
    {
        // A pid that cannot be running: process ids are positive, and this range is not handed out to
        // a live user process on either platform in a way that could collide with a real Director.
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000020",
            pid: 999_999_996);

        Assert.Equal(DirectorResolution.NotRunning, Locator().Resolve().Outcome);
    }

    /// <summary>
    /// THE PROCESS-ID REUSE CHECK. The registration names a pid that IS alive, but that process started
    /// well after the registration was written - so it inherited the id from the Director that wrote
    /// the file rather than being it. Without the start-time comparison this resolves to a live
    /// process and the launcher goes on to shut down something it does not own.
    /// </summary>
    [Fact]
    public void ProcessStartTime_AfterTheRegistration_IsRejectedAsARecycledProcessId()
    {
        // Registered an hour before this process started, which no true author ever is.
        var beforeThisProcessStarted =
            Process.GetCurrentProcess().StartTime.ToUniversalTime() - TimeSpan.FromHours(1);
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000030",
            startedAt: beforeThisProcessStarted);

        Assert.Equal(DirectorResolution.NotRunning, Locator().Resolve().Outcome);
    }

    /// <summary>
    /// The other direction of the same check: a registration written far in the future cannot have been
    /// written by a process that started long before it. A guard has two failure directions and only
    /// asserting one of them leaves half of it untested.
    /// </summary>
    [Fact]
    public void ProcessStartTime_LongBeforeTheRegistration_IsAlsoRejected()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000031",
            startedAt: DateTime.UtcNow + TimeSpan.FromDays(1));

        Assert.Equal(DirectorResolution.NotRunning, Locator().Resolve().Outcome);
    }

    /// <summary>
    /// A pre-1.8 Director registers at the storage root rather than in an instance folder, and it is
    /// still the Director this launcher supervises. Two layouts, one Director.
    /// </summary>
    [Fact]
    public void APreInstanceLayoutRegistration_IsStillTheSupervisedDirector()
    {
        WriteRegistration(FlatDirectory, "aaaa0001-0000-0000-0000-000000000040", version: "1.7.2");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("1.7.2", lookup.Director!.Version);
    }

    [Fact]
    public void AMalformedRegistration_IsSkippedRatherThanFailingTheLookup()
    {
        Directory.CreateDirectory(InstanceDirectory("default"));
        File.WriteAllText(Path.Combine(InstanceDirectory("default"), "broken.json"), "{ this is not json");
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000050");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("aaaa0001-0000-0000-0000-000000000050", lookup.Director!.DirectorId);
    }

    [Fact]
    public void TheSessionCount_ComesFromTheRostersTheResolvedDirectorMaintains()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000060");
        WriteRoster(InstanceHome("default"), "aaaa0001-0000-0000-0000-000000000060", sessions: 3);

        var locator = Locator();
        var lookup = locator.Resolve();

        Assert.Equal(3, locator.ReadSessionCount(lookup.Director!));
    }

    /// <summary>
    /// An idle Director maintains a roster with nothing in it, which is a genuine zero and must read as
    /// one. The distinction from the case below is the whole reason the count is nullable.
    /// </summary>
    [Fact]
    public void AnEmptyRoster_ReadsAsZeroSessions()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000061");
        WriteRoster(InstanceHome("default"), "aaaa0001-0000-0000-0000-000000000061", sessions: 0);

        var locator = Locator();
        Assert.Equal(0, locator.ReadSessionCount(locator.Resolve().Director!));
    }

    /// <summary>
    /// NO ROSTER IS NOT NO SESSIONS. A missing file means the answer is unknown, and an unknown answer
    /// must never let an update proceed - the update owner turns this null into HeldBecauseUnknown. A
    /// zero here would silently restart a Director holding live work.
    /// </summary>
    [Fact]
    public void AMissingRoster_ReadsAsUnknownRatherThanZero()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000062");

        var locator = Locator();
        Assert.Null(locator.ReadSessionCount(locator.Resolve().Director!));
    }

    /// <summary>
    /// The roster of a PRE-1.8 Director lives under the storage root, not under an instance folder, and
    /// the count has to be read from wherever that Director actually keeps it. This is why the home
    /// travels on the resolved Director rather than being assumed from the locator's own instance.
    /// </summary>
    [Fact]
    public void TheSessionCountOfAPreInstanceLayoutDirector_IsReadFromTheRoot()
    {
        WriteRegistration(FlatDirectory, "aaaa0001-0000-0000-0000-000000000070");
        WriteRoster(_root, "aaaa0001-0000-0000-0000-000000000070", sessions: 2);

        var locator = Locator();
        Assert.Equal(2, locator.ReadSessionCount(locator.Resolve().Director!));
    }
}
