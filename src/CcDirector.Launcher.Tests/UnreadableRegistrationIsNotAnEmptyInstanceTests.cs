using System.ComponentModel;
using CcDirector.Core.Instances;
using CcDirector.Core.Update;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Issue #2730: a registration the locator cannot read must never come back as an EMPTY instance home.
///
/// THE DEFECT, AND WHY IT WAS LIVE RATHER THAN LATENT. The locator skipped a registration it could not
/// parse and a live process that would not say when it started - correctly, because neither can be
/// certified and an uncertified process must never be stopped or updated over. The skips then vanished,
/// so with nothing left the answer was <c>NotRunning</c>: "I could not read what is there" and "nothing
/// is there" were one answer.
///
/// That answer has three consumers on main and TWO TAKE IT OPPOSITE WAYS. <c>DirectorSupervisor.StopAsync</c>
/// declines on it, which is careful. <c>DirectorSupervisor.Start</c> reads it as permission and STARTS A
/// DIRECTOR. So one corrupt registration file was enough to put a second Director on an instance home
/// that already had a live one - the shape of the failure that corrupted a database and took the hosted
/// service down for thirty-two minutes on 30 July 2026.
///
/// SO EVERY TEST HERE ASSERTS THE START PATH AS WELL AS THE STOP PATH, and that is the whole point of the
/// file rather than a completeness habit. The same value is safe in one and dangerous in the other, so a
/// suite that covered only the stop path would go green while the live defect sat untouched - which is
/// precisely the trap this defect is made of.
///
/// THE KNOWN-BAD INPUT IS A REAL CORRUPTED FILE, not a mocked read. The bytes written below are what a
/// half-written or truncated registration actually looks like on disk, and the production locator parses
/// them with the production parser.
/// </summary>
[Collection(StorageRootCollection.Name)]
public sealed class UnreadableRegistrationIsNotAnEmptyInstanceTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly string? _previousInstancesDir;

    public UnreadableRegistrationIsNotAnEmptyInstanceTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _previousInstancesDir = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", null);

        _root = Path.Combine(Path.GetTempPath(), "cc-2730-" + Guid.NewGuid().ToString("N"));
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

    private string InstanceHome => Path.Combine(_root, "instances", InstanceContext.DefaultSlug);
    private string InstanceDirectory => Path.Combine(InstanceHome, "config", "director", "instances");
    private string FlatDirectory => Path.Combine(_root, "config", "director", "instances");

    private DirectorInstanceLocator Locator() =>
        new(InstanceHome, FlatDirectory, Environment.ProcessPath ?? "");

    /// <summary>A registration that is genuinely corrupt on disk - truncated mid-object, which is what a
    /// process killed while writing one leaves behind.</summary>
    private void WriteTruncatedRegistration(string name = "aaaa0001-0000-0000-0000-000000000001")
    {
        Directory.CreateDirectory(InstanceDirectory);
        File.WriteAllText(Path.Combine(InstanceDirectory, name + ".json"),
            "{\"DirectorId\": \"aaaa0001-0000-0000-0000-000000000001\", \"Pid\": 4");
    }

    /// <summary>Valid JavaScript Object Notation that is not a registration. It parses, and it names no
    /// Director - the same class of fault, and it does not become safe because the parser succeeded.</summary>
    private void WriteRegistrationNamingNoDirector(string name = "aaaa0002-0000-0000-0000-000000000002")
    {
        Directory.CreateDirectory(InstanceDirectory);
        File.WriteAllText(Path.Combine(InstanceDirectory, name + ".json"), "{\"note\": \"not a registration\"}");
    }

    /// <summary>A registration exactly as a live Director writes one, naming this test process.</summary>
    private void WriteLiveRegistration(string directorId)
    {
        Directory.CreateDirectory(InstanceDirectory);
        File.WriteAllText(Path.Combine(InstanceDirectory, directorId + ".json"), $$"""
        {
          "DirectorId": "{{directorId}}",
          "Pid": {{Environment.ProcessId}},
          "StartedAt": "{{DateTime.UtcNow:o}}",
          "Version": "2.0.6"
        }
        """);
    }

    // =========================================================================================
    // The locator: could-not-read is its own answer
    // =========================================================================================

    /// <summary>
    /// THE KNOWN-BAD INPUT, AND THE ASSERTION THE WHOLE ISSUE TURNS ON. One corrupt registration and
    /// nothing else. Before the fix this resolved NotRunning - indistinguishable from an empty home.
    /// </summary>
    [Fact]
    public void One_corrupt_registration_does_not_resolve_NotRunning()
    {
        WriteTruncatedRegistration();

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.NotEqual(DirectorResolution.NotRunning, lookup.Outcome);
        Assert.Null(lookup.Director);

        // It shows its evidence rather than asserting a cause, and says the one thing a reader must not
        // conclude.
        Assert.Single(lookup.Unreadable);
        Assert.Contains("could not be read", lookup.Conflict);
        Assert.Contains("NOT an empty instance home", lookup.Conflict);
    }

    /// <summary>Valid notation naming no Director is the same answer. A parser that succeeded still left
    /// us unable to say what process, if any, that file stands for.</summary>
    [Fact]
    public void A_registration_that_parses_and_names_no_Director_is_also_Unknown()
    {
        WriteRegistrationNamingNoDirector();

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.Contains("names no usable Director", string.Join(" ", lookup.Unreadable));
    }

    /// <summary>
    /// THE CONTROL, and without it the test above proves nothing. A genuinely empty instance home must
    /// still answer NotRunning - otherwise the fix would simply have made every answer Unknown, which
    /// would look like a pass on the test above and would stop the launcher ever starting a Director.
    /// </summary>
    [Fact]
    public void An_empty_instance_home_still_resolves_NotRunning()
    {
        Directory.CreateDirectory(InstanceDirectory);

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.NotRunning, lookup.Outcome);
        Assert.Empty(lookup.Unreadable);
        Assert.Null(lookup.Conflict);
    }

    /// <summary>
    /// A corrupt registration BESIDE a live one still resolves the live one - the fix must not make an
    /// unrelated bad file stop a working machine. The corruption is still carried on the answer, for the
    /// same reason a resolved tie-break carries its conflict: the machine is in a state that should not
    /// exist, and it becomes an Unknown the moment the good registration goes.
    /// </summary>
    [Fact]
    public void A_corrupt_registration_beside_a_live_one_resolves_the_live_one_and_still_reports_the_corruption()
    {
        WriteLiveRegistration("bbbb0001-0000-0000-0000-000000000001");
        WriteTruncatedRegistration("cccc0001-0000-0000-0000-000000000001");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("bbbb0001-0000-0000-0000-000000000001", lookup.Director!.DirectorId);
        Assert.Single(lookup.Unreadable);
    }

    // =========================================================================================
    // BOTH consumers. The same value is safe in one and dangerous in the other.
    // =========================================================================================

    /// <summary>
    /// WHAT USED TO BE HERE, AND WHY IT IS GONE RATHER THAN AMENDED.
    ///
    /// This slot held a test asserting that Start REFUSES on an unreadable claim. That behaviour was
    /// reversed deliberately: refusing converted a recoverable state into a permanent one, and it was
    /// defending against a duplicate that the Director-s own SingleInstanceGuard already prevents. The
    /// test is deleted rather than inverted in place, because a test whose name still describes the old
    /// promise is worse than no test - the next reader trusts the name.
    ///
    /// The behaviour that replaced it is asserted by
    /// <see cref="A_corrupt_file_left_behind_by_a_stop_does_not_stop_the_Director_starting_again"/>, and
    /// the START path is still covered for the cases where something IS demonstrably there:
    /// <see cref="An_ambiguous_instance_home_does_not_let_Start_add_another_Director"/> and
    /// <see cref="An_unsupervised_process_holding_the_instance_home_does_not_let_Start_add_another_Director"/>.
    /// </summary>

    /// <summary>
    /// THE PARTNER THAT MAKES THE ONE ABOVE MEAN SOMETHING. An empty instance home is a genuine
    /// NotRunning, so Start SHOULD go ahead - and the rig's fake executable makes that attempt visible as
    /// an exception. If this ever stops throwing, the test above has become an assertion about nothing
    /// and both must be re-examined rather than trusted.
    /// </summary>
    [Fact]
    public void An_empty_instance_home_lets_Start_attempt_a_start()
    {
        if (!OperatingSystem.IsWindows()) return;

        var layout = FakeInstalledDirector();
        Directory.CreateDirectory(InstanceDirectory);
        var supervisor = new DirectorSupervisor(layout, Locator());

        Assert.Throws<Win32Exception>(() => supervisor.Start());
    }

    /// <summary>
    /// AN AMBIGUOUS INSTANCE HOME MUST NOT LET START PUT A THIRD DIRECTOR IN IT.
    ///
    /// THIS GUARD HAD NEVER BEEN TESTED, ON ANY BRANCH, AND THE MUTATION SWEEP IS WHAT FOUND THAT.
    /// Inverting <c>Start</c>'s guard from "anything but NotRunning" to "only Running" survived every
    /// test in this file, because the Unknown case is also caught by the explicit branch above it - so
    /// the general guard, which is the only thing standing between two live claimants and a third
    /// process, was load-bearing and unasserted. There was no test that called <c>Start</c> at all
    /// before this file; the rig here is the first thing able to drive it, so closing the gap costs two
    /// tests and is worth far more than the one it was written for.
    /// </summary>
    [Fact]
    public void An_ambiguous_instance_home_does_not_let_Start_add_another_Director()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Two live registrations naming this process. Both certify against the locator's installed path,
        // so neither can be told from the other and the answer is Ambiguous.
        WriteLiveRegistration("dddd0001-0000-0000-0000-000000000001");
        WriteLiveRegistration("dddd0002-0000-0000-0000-000000000002");
        var locator = Locator();
        Assert.Equal(DirectorResolution.Ambiguous, locator.Resolve().Outcome);

        new DirectorSupervisor(FakeInstalledDirector(), locator).Start();
    }

    /// <summary>
    /// And a live process that is NOT the supervised image. Something is sitting in that instance home,
    /// so nothing new may be started on top of it - even though this launcher may not end it either.
    /// </summary>
    [Fact]
    public void An_unsupervised_process_holding_the_instance_home_does_not_let_Start_add_another_Director()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteLiveRegistration("eeee0001-0000-0000-0000-000000000001");
        // The locator's installed path points somewhere this process is not, so the lone claimant is
        // live, resolved, and refused as not ours to act on.
        var locator = new DirectorInstanceLocator(InstanceHome, FlatDirectory,
            Path.Combine(_root, "somewhere-else", "cc-director.exe"));
        Assert.Equal(DirectorResolution.NotSupervised, locator.Resolve().Outcome);

        new DirectorSupervisor(FakeInstalledDirector(), locator).Start();
    }

    /// <summary>
    /// And the same input in the STOP path, where the identical value is safe. It declines, as it always
    /// did - what the fix adds here is that it declines for the right stated reason instead of printing
    /// "more than one live process claims this instance", which would send the next reader looking for a
    /// second process that does not exist.
    /// </summary>
    [Fact]
    public async Task One_corrupt_registration_makes_StopAsync_decline()
    {
        var layout = FakeInstalledDirector();
        WriteTruncatedRegistration();
        var supervisor = new DirectorSupervisor(layout, Locator());

        // Nothing to stop and nothing certifiable, so this must complete without touching a process.
        await supervisor.StopAsync();

        // The evidence the refusal was the Unknown one, taken from the locator the supervisor read.
        var lookup = supervisor.Locator.Resolve();
        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
    }

    /// <summary>
    /// The third consumer. <c>IsRunning</c> answers "is this instance home occupied", and the only
    /// decision it drives is whether to start another Director - so an unreadable claim must count as
    /// occupied. Reported false, it would be the same fail-open one level up.
    /// </summary>
    [Fact]
    public void One_corrupt_registration_makes_IsRunning_report_occupied()
    {
        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), Locator());
        Directory.CreateDirectory(InstanceDirectory);
        Assert.False(supervisor.IsRunning);   // control: genuinely empty

        WriteTruncatedRegistration();
        Assert.True(supervisor.IsRunning);    // and now something is there that could not be read
    }

    /// <summary>
    /// The update owner must hold rather than install. It reads <c>ReadStatus</c>, which reports null for
    /// anything that is not a single resolved Director - so an unreadable claim holds the update, which is
    /// the same rule it already applies to an ambiguous machine.
    /// </summary>
    [Fact]
    public void One_corrupt_registration_leaves_the_Director_status_unreadable_so_an_update_holds()
    {
        WriteTruncatedRegistration();
        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), Locator());

        Assert.Null(supervisor.ReadStatus());
    }

    /// <summary>
    /// An installed Director that EXISTS as a file and is not a runnable program. It lets a test drive
    /// the real <c>Start</c> past its exe-exists guard and observe its decision, without any possibility
    /// of a Director actually being launched by a test run.
    /// </summary>
    private InstallLayout FakeInstalledDirector()
    {
        var layout = new InstallLayout(_root);
        var exe = layout.PathFor(ComponentRegistry.Director);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "this is not a program");
        return layout;
    }

    /// <summary>
    /// The "never null" promise on <c>DirectorLookup.Unreadable</c>, asserted rather than asserted-in-a-
    /// comment. Found by reading the safety sentences in this change against the code directly under
    /// them: written as an auto-property initializer the fallback ran only on the primary constructor,
    /// so a <c>with</c> expression could put a null straight through the sentence promising it could not.
    /// </summary>
    [Fact]
    public void The_Unreadable_list_is_never_null_even_through_a_with_expression()
    {
        var lookup = new DirectorLookup(DirectorResolution.NotRunning, null, Array.Empty<string>());
        Assert.NotNull(lookup.Unreadable);

        // The null is forced deliberately. The compiler's nullable analysis says a caller should not do
        // this, and the promise in the property's own summary is that doing it anyway cannot produce a
        // null - which is a claim about runtime, so it is asserted at runtime.
        IReadOnlyList<string> forcedNull = null!;
        var nulled = lookup with { Unreadable = forcedNull };
        Assert.NotNull(nulled.Unreadable);
        Assert.Empty(nulled.Unreadable);
    }

    // =========================================================================================
    // The fix round. Every one of these comes from an independent review, and every one of them
    // is a hole this change LEFT OPEN or, in the last case, one it OPENED.
    // =========================================================================================

    /// <summary>
    /// A FIXTURE-SHAPED BLIND SPOT, and the reason it matters more than the assertion it adds.
    ///
    /// Every test above writes exactly ONE corrupt registration. So the guard could be narrowed from
    /// "more than none" to "exactly one" and the whole file still passed - the mutation survived not
    /// because the code was right but because every fixture looked the same. Two corrupt files is the
    /// ordinary case on a machine that has crashed twice, and it would have resolved NotRunning again.
    ///
    /// The lesson is about the fixture rather than the assertion: varying the SHAPE of the input finds
    /// what another assertion on the same input cannot.
    /// </summary>
    [Fact]
    public void TWO_corrupt_registrations_are_also_Unknown_not_NotRunning()
    {
        WriteTruncatedRegistration("aaaa0001-0000-0000-0000-000000000001");
        WriteTruncatedRegistration("aaaa0002-0000-0000-0000-000000000002");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.Equal(2, lookup.Unreadable.Count);
    }

    /// <summary>Three, for the same reason - the count is not a magic number in either direction.</summary>
    [Fact]
    public void THREE_corrupt_registrations_are_also_Unknown()
    {
        for (var i = 1; i <= 3; i++)
            WriteTruncatedRegistration($"aaaa000{i}-0000-0000-0000-00000000000{i}");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.Equal(3, lookup.Unreadable.Count);
    }

    /// <summary>
    /// AN UNREADABLE DIRECTORY, WHICH WAS THE SAME FOLD ONE LAYER UP. Directory.Exists answers FALSE for
    /// a directory that exists and cannot be reached, so "not there" and "not readable" came back as one
    /// answer - and the listing catch then continued without recording anything either. A directory of
    /// registrations nobody can see is the strongest possible reason not to call the home empty.
    ///
    /// Driven by putting a FILE where the directory belongs, which is a real on-disk state and makes the
    /// listing fail without needing a permission change this rig cannot make.
    /// </summary>
    [Fact]
    public void A_registrations_directory_that_cannot_be_listed_is_Unknown_not_NotRunning()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstanceDirectory)!);
        File.WriteAllText(InstanceDirectory, "this is a file where a directory belongs");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.Contains("could not be listed", string.Join(" ", lookup.Unreadable));
    }

    /// <summary>
    /// THE CONTROL for the test above, and it is what makes that one evidence. A registrations directory
    /// that genuinely does not exist must still be NotRunning - otherwise the fix would simply have made
    /// every absent directory Unknown, which passes the test above and stops the launcher ever starting a
    /// Director on a fresh machine.
    /// </summary>
    [Fact]
    public void A_registrations_directory_that_does_not_exist_is_still_NotRunning()
    {
        // Nothing created at all: neither the instance home nor the legacy flat path exists.
        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.NotRunning, lookup.Outcome);
        Assert.Empty(lookup.Unreadable);
    }

    /// <summary>
    /// A REGISTRATION WITH NO START TIME MADE Resolve THROW, against the "never throws" contract in its
    /// own summary. An unset stamp is DateTime.MinValue, and subtracting the registration lag from it
    /// throws outside every catch in the file.
    ///
    /// It is also right on the merits: the stamp is the ONLY thing that tells this Director from a
    /// process that inherited its process id, so a registration without one certifies nothing and
    /// belongs with the others that could not be read.
    /// </summary>
    [Fact]
    public void A_registration_with_no_start_time_does_not_throw_and_is_Unknown()
    {
        Directory.CreateDirectory(InstanceDirectory);
        File.WriteAllText(Path.Combine(InstanceDirectory, "ffff0001-0000-0000-0000-000000000001.json"),
            $$"""
            {
              "DirectorId": "ffff0001-0000-0000-0000-000000000001",
              "Pid": {{Environment.ProcessId}},
              "Version": "2.0.6"
            }
            """);

        var lookup = Locator().Resolve();   // must not throw

        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.Contains("startedAt=missing", string.Join(" ", lookup.Unreadable));
    }

    /// <summary>
    /// THE REGRESSION THIS FIX INTRODUCED, WHICH WAS WORSE THAN THE DEFECT IT FIXES.
    ///
    /// A corrupt file beside one good live Director resolves Running, so an update pass would proceed:
    /// it stops the Director - which deletes that Director-s own registration - and then only the
    /// corrupt file is left, at which point Start REFUSES. The health wait times out, the rollback tries
    /// the same refused start, and a formerly working Director is left down with every relaunch
    /// refusing. Before the fix, the corrupt file was ignored and the Director came back.
    ///
    /// A fix that turns a recoverable state into a permanent one is not a fix. So the evidence has to
    /// reach the DECISION rather than stopping at the locator: DirectorStatus carries it, and the update
    /// owner holds on it.
    /// </summary>
    [Fact]
    public void A_corrupt_registration_beside_a_live_Director_reaches_the_status_the_update_owner_reads()
    {
        WriteLiveRegistration("bbbb0002-0000-0000-0000-000000000002");
        WriteTruncatedRegistration("cccc0002-0000-0000-0000-000000000002");
        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), Locator());

        var status = supervisor.ReadStatus();

        Assert.NotNull(status);
        Assert.Equal("bbbb0002-0000-0000-0000-000000000002", status!.DirectorId);

        // THE ASSERTION THE REGRESSION TURNED ON. Without this the update owner cannot know, and it
        // stops a Director it will not be able to start again.
        Assert.Single(status.Unreadable);
        Assert.Contains("could not be read", string.Join(" ", status.Unreadable));
    }

    /// <summary>And the control: a clean machine carries nothing, so a normal update is not held.</summary>
    [Fact]
    public void A_live_Director_with_nothing_unreadable_carries_an_empty_list()
    {
        WriteLiveRegistration("bbbb0003-0000-0000-0000-000000000003");
        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), Locator());

        var status = supervisor.ReadStatus();

        Assert.NotNull(status);
        Assert.Empty(status!.Unreadable);
    }

    /// <summary>The same never-null promise on DirectorStatus as on DirectorLookup, and for the same
    /// reason - the setter coalesces, so a with-expression cannot put a null through the sentence.</summary>
    [Fact]
    public void The_status_Unreadable_list_is_never_null_even_through_a_with_expression()
    {
        var status = new DirectorStatus("d-1", 1, "2.0.6", 0);
        Assert.NotNull(status.Unreadable);

        IReadOnlyList<string> forcedNull = null!;
        Assert.Empty((status with { Unreadable = forcedNull }).Unreadable);
    }

    // =========================================================================================
    // THE PROPERTY, ASSERTED DIRECTLY
    // =========================================================================================

    /// <summary>
    /// NO READING OF A CORRUPT FILE MAY LEAVE A FORMERLY WORKING DIRECTOR UNABLE TO START.
    ///
    /// The property, driven end to end rather than argued: a corrupt file beside a live Director, then
    /// the stop that removes that Director-s own registration, then the start - and the start must
    /// reach its launch attempt rather than refusing.
    ///
    /// THIS TEST FAILED AGAINST THE FIRST DRAFT OF THIS FIX, which is why it exists. That draft refused
    /// to start on an unreadable claim, so after a stop the machine was left with only the corrupt file,
    /// every relaunch refused, and a working Director stayed down. Converting a recoverable state into a
    /// permanent one is worse than the fail-open it replaced.
    ///
    /// WHAT ACTUALLY PREVENTS A DUPLICATE, since it is not this refusal: SingleInstanceGuard, keyed on
    /// the exe path slot and acquired in the Director-s own startup. A second Director from the installed
    /// exe raises the existing window and exits. That works whether or not this launcher could read a
    /// registration file, which is exactly why a refusal here bought nothing and cost everything.
    /// </summary>
    [Fact]
    public void A_corrupt_file_left_behind_by_a_stop_does_not_stop_the_Director_starting_again()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The state a stop leaves behind: the good registration is gone (a Director deletes its own on
        // shutdown) and the corrupt one is still there.
        WriteTruncatedRegistration("dddd0009-0000-0000-0000-000000000009");
        var locator = Locator();
        Assert.Equal(DirectorResolution.Unknown, locator.Resolve().Outcome);

        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), locator);

        // It must REACH the launch. The rig turns a launch attempt into Win32Exception, so throwing is
        // the pass here and returning quietly is the failure - the exact inverse of the refusal tests
        // above, and the reason the rig was built to make both observable.
        Assert.Throws<Win32Exception>(() => supervisor.Start());
    }

    // =========================================================================================
    // THE MIXED DIRECTORY, AS A FIRST-CLASS FIXTURE
    //
    // Both review rounds missed something for the same reason: every fixture in the first draft had
    // the corrupt file as the ONLY file. That shape is what let "more than none" survive being
    // narrowed to "exactly one", and it is what hid the Running-with-unreadable-evidence state from
    // the consumer that decides whether a binary swap is safe. Varying the SHAPE of the input is what
    // found both; another assertion on the same input would have found neither.
    // =========================================================================================

    /// <summary>
    /// A mixed directory: two good live registrations and two corrupt ones. It must resolve the live
    /// pair through the ordinary tie-break, AND carry both unreadable claims.
    /// </summary>
    [Fact]
    public void A_mixed_directory_resolves_its_live_registrations_and_carries_every_unreadable_claim()
    {
        WriteLiveRegistration("eeee0011-0000-0000-0000-000000000011");
        WriteLiveRegistration("eeee0012-0000-0000-0000-000000000012");
        WriteTruncatedRegistration("ffff0011-0000-0000-0000-000000000011");
        WriteRegistrationNamingNoDirector("ffff0012-0000-0000-0000-000000000012");

        var lookup = Locator().Resolve();

        // Two live claimants of the same image is the pre-existing ambiguous case, untouched.
        Assert.Equal(DirectorResolution.Ambiguous, lookup.Outcome);
        Assert.Equal(2, lookup.Unreadable.Count);
    }

    /// <summary>
    /// AND THE CONSUMER THAT MATTERS SEES IT. This is the state the independent review found: one good
    /// live registration plus corrupt ones resolves Running, and the update owner reads DirectorStatus -
    /// so if the evidence stops at the locator, a binary swap is authorized on a home the launcher could
    /// not fully read. The fold had moved rather than gone: Running-plus-unreadable became a clean status.
    /// </summary>
    [Fact]
    public void A_mixed_directory_carries_its_unreadable_claims_all_the_way_to_the_update_decision()
    {
        WriteLiveRegistration("eeee0013-0000-0000-0000-000000000013");
        WriteTruncatedRegistration("ffff0013-0000-0000-0000-000000000013");
        WriteRegistrationNamingNoDirector("ffff0014-0000-0000-0000-000000000014");
        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), Locator());

        var status = supervisor.ReadStatus();

        Assert.NotNull(status);
        Assert.Equal("eeee0013-0000-0000-0000-000000000013", status!.DirectorId);
        Assert.Equal(2, status.Unreadable.Count);
    }

    /// <summary>
    /// A mixed directory whose only LIVE registration is removed becomes Unknown, not NotRunning - which
    /// is the transition a stop performs, and the moment the whole defect used to fire.
    /// </summary>
    [Fact]
    public void A_mixed_directory_becomes_Unknown_when_its_live_registration_goes()
    {
        WriteLiveRegistration("eeee0014-0000-0000-0000-000000000014");
        WriteTruncatedRegistration("ffff0015-0000-0000-0000-000000000015");
        Assert.Equal(DirectorResolution.Running, Locator().Resolve().Outcome);

        // What a clean shutdown does: the Director deletes its own registration.
        File.Delete(Path.Combine(InstanceDirectory, "eeee0014-0000-0000-0000-000000000014.json"));

        var lookup = Locator().Resolve();
        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.Single(lookup.Unreadable);
    }

    // =========================================================================================
    // A PROCESS THAT IS ALIVE AND CANNOT BE INSPECTED
    //
    // The mutation sweep found this gap rather than a reviewer: the fix for it was written and had
    // NO test, so removing the fix survived the whole suite. A live process the operating system
    // will not describe is not a dead one, and erasing its claim is the same fail-open the rest of
    // this file is about - one method further down.
    // =========================================================================================

    /// <summary>
    /// A registration naming a REAL live process this test cannot inspect.
    ///
    /// PID 4 on Windows is the System process: it exists, it is running, and a normal user process is
    /// refused when it asks when that process started. So this is a genuine uninspectable-live-process
    /// input rather than a mock - which matters, because the branch under test exists precisely for a
    /// state the operating system produces and a fake would not.
    ///
    /// Either of the two guards may catch it - the inspection itself, or the start-time read - and the
    /// test deliberately does not care which: both record an unreadable claim, and asserting on the
    /// specific one would pin an implementation detail rather than the property. What must NOT happen is
    /// the claim being erased and the answer coming back NotRunning.
    /// </summary>
    [Fact]
    public void A_live_process_that_cannot_be_inspected_is_Unknown_not_NotRunning()
    {
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(InstanceDirectory);
        File.WriteAllText(Path.Combine(InstanceDirectory, "9999aaaa-0000-0000-0000-000000000001.json"),
            $$"""
            {
              "DirectorId": "9999aaaa-0000-0000-0000-000000000001",
              "Pid": 4,
              "StartedAt": "{{DateTime.UtcNow:o}}",
              "Version": "2.0.6"
            }
            """);

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.NotEmpty(lookup.Unreadable);
        Assert.Null(lookup.Director);
    }

    /// <summary>
    /// THE CONTROL that makes the test above evidence rather than a coincidence. The SAME registration
    /// shape naming a process this test CAN inspect - itself - resolves normally. Without this, the test
    /// above would pass equally well if the locator had simply started answering Unknown for everything.
    /// </summary>
    [Fact]
    public void The_same_registration_shape_naming_an_inspectable_process_resolves_normally()
    {
        WriteLiveRegistration("9999bbbb-0000-0000-0000-000000000001");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Empty(lookup.Unreadable);
    }

    /// <summary>
    /// And a registration naming a process id that is POSITIVELY DEAD is still NotRunning - the third of
    /// the four answers, and the one that must not be swept up with the other two. A dead process id is a
    /// FACT about the world; an uninspectable one is the absence of a fact.
    /// </summary>
    [Fact]
    public void A_registration_naming_a_dead_process_id_is_still_NotRunning()
    {
        Directory.CreateDirectory(InstanceDirectory);
        // A process id that is real in shape and certain not to be running: the maximum Windows allows
        // is far below this, so the operating system answers "no such process" rather than refusing.
        File.WriteAllText(Path.Combine(InstanceDirectory, "9999cccc-0000-0000-0000-000000000001.json"),
            $$"""
            {
              "DirectorId": "9999cccc-0000-0000-0000-000000000001",
              "Pid": 2147483646,
              "StartedAt": "{{DateTime.UtcNow:o}}",
              "Version": "2.0.6"
            }
            """);

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.NotRunning, lookup.Outcome);
        Assert.Empty(lookup.Unreadable);
    }

    // =========================================================================================
    // BOUNDARY TIMESTAMPS - the carried finding, on the input the first fix did not consider
    // =========================================================================================

    /// <summary>
    /// A PARSEABLE registration whose timestamp sits at either arithmetic boundary must not make Resolve
    /// throw. The first draft of the completeness guard tried to prove the subtraction safe BY PERFORMING
    /// IT, so a stamp just above the minimum threw inside the guard meant to reject it; and a stamp
    /// within the skew of the maximum survived that guard entirely and threw on the addition in Resolve.
    ///
    /// THE HARM IS THE PROPERTY, NOT THE EXCEPTION. Resolve is called by Start, StopAsync, ReadStatus and
    /// IsRunning with no local recovery, so one such file would keep a stopped Director from starting
    /// until somebody deleted it by hand - a corrupt reading leaving a formerly working thing unable to
    /// start, which is the thing that may not happen.
    ///
    /// The earlier test covered a TRUNCATED document only, which never reaches this arithmetic at all.
    /// </summary>
    [Theory]
    [InlineData("0001-01-01T00:00:01.0000000Z")]   // just above DateTime.MinValue: threw in the guard
    [InlineData("0001-01-01T00:05:00.0000000Z")]   // inside the registration lag: threw in the guard
    [InlineData("9999-12-31T23:59:59.0000000Z")]   // within the skew of DateTime.MaxValue: threw in Resolve
    [InlineData("9999-12-31T23:59:58.5000000Z")]
    public void A_registration_at_an_arithmetic_boundary_does_not_throw_and_is_Unknown(string stamp)
    {
        Directory.CreateDirectory(InstanceDirectory);
        File.WriteAllText(Path.Combine(InstanceDirectory, "bbbb9999-0000-0000-0000-000000000001.json"),
            $$"""
            {
              "DirectorId": "bbbb9999-0000-0000-0000-000000000001",
              "Pid": {{Environment.ProcessId}},
              "StartedAt": "{{stamp}}",
              "Version": "2.0.6"
            }
            """);

        var lookup = Locator().Resolve();   // must not throw

        Assert.Equal(DirectorResolution.Unknown, lookup.Outcome);
        Assert.NotEmpty(lookup.Unreadable);
    }

    /// <summary>
    /// AND THE PROPERTY AT THAT INPUT: a boundary-timestamped file left behind must not stop the Director
    /// starting. This is the harm the finding named, asserted rather than reasoned about.
    /// </summary>
    [Fact]
    public void A_boundary_timestamp_file_does_not_stop_the_Director_starting()
    {
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(InstanceDirectory);
        File.WriteAllText(Path.Combine(InstanceDirectory, "bbbb9998-0000-0000-0000-000000000001.json"),
            $$"""
            {
              "DirectorId": "bbbb9998-0000-0000-0000-000000000001",
              "Pid": {{Environment.ProcessId}},
              "StartedAt": "0001-01-01T00:00:01.0000000Z",
              "Version": "2.0.6"
            }
            """);

        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), Locator());

        // Reaching the launch is the pass; the rig turns the attempt into Win32Exception.
        Assert.Throws<Win32Exception>(() => supervisor.Start());
    }

    /// <summary>The control: an ordinary timestamp still resolves normally, so the boundary guard has not
    /// simply started rejecting everything.</summary>
    [Fact]
    public void An_ordinary_timestamp_still_resolves_normally()
    {
        WriteLiveRegistration("bbbb9997-0000-0000-0000-000000000001");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Empty(lookup.Unreadable);
    }

    // =========================================================================================
    // THE UPDATE DECISION ITSELF - closing a test that named a decision it never invoked
    // =========================================================================================

    /// <summary>
    /// THE UPDATE OWNER HOLDS, driven through the real DirectorUpdateOwner rather than stopping at the
    /// status it reads.
    ///
    /// WHY THIS EXISTS AS ITS OWN TEST. A reviewer found that the test named
    /// "...all_the_way_to_the_update_decision" called only ReadStatus and never invoked the owner - it
    /// proved the evidence was CARRIED, and named a decision it did not reach. That is worse than a
    /// missing test: it is the evidence for a binding property, claiming a scope it does not have. The
    /// carrying test keeps its narrower name; this one performs the decision.
    /// </summary>
    [Fact]
    public async Task The_update_owner_HOLDS_when_the_instance_home_has_an_unreadable_claim()
    {
        WriteLiveRegistration("aaaa7001-0000-0000-0000-000000000001");
        WriteTruncatedRegistration("aaaa7002-0000-0000-0000-000000000002");
        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), Locator());

        // THE PRECONDITION, ASSERTED - and this is what the first version of this test was missing.
        // HeldBecauseUnknown is returned by TWO branches: the unreadable-CLAIM hold under test, and the
        // unreadable-ROSTER hold below it. Asserting the enum alone cannot tell them apart, so a
        // mutation DISABLING the claim hold survived the whole suite - the roster branch produced the
        // same answer. Found by a reviewer substituting that constant in the working tree.
        //
        // With a readable roster saying ZERO, the only branch left that can return HeldBecauseUnknown
        // is the one this test is named for.
        StageUpdateAndRosters(supervisor);
        Assert.Equal(0, supervisor.ReadStatus()!.Sessions);

        var decision = await new DirectorUpdateOwner(supervisor).RunOnceAsync();

        Assert.Equal(DirectorUpdateDecision.HeldBecauseUnknown, decision);
    }

    /// <summary>
    /// THE CONTROL, and the test above is worth nothing without it: the SAME staged update, the SAME live
    /// Director, and NO unreadable claim must NOT be held for this reason. Otherwise the hold could be
    /// firing for any reason at all and the test would still pass.
    /// </summary>
    [Fact]
    public async Task The_update_owner_does_NOT_hold_for_this_reason_when_nothing_is_unreadable()
    {
        WriteLiveRegistration("aaaa7003-0000-0000-0000-000000000003");
        var supervisor = new DirectorSupervisor(FakeInstalledDirector(), Locator());

        var decision = await RunUpdateOwnerAsync(supervisor);

        Assert.NotEqual(DirectorUpdateDecision.HeldBecauseUnknown, decision);
    }

    /// <summary>
    /// Stage a real update for the installed Director this rig fakes, give the live Director an EXPLICIT
    /// empty roster so "how busy is it" has a real answer, and run one pass of the production
    /// <see cref="DirectorUpdateOwner"/>.
    ///
    /// The roster is explicit rather than absent on purpose: an absent roster is itself an unknown, and
    /// the unreadable-ROSTER hold returns the SAME DirectorUpdateDecision as the unreadable-CLAIM hold
    /// under test. Without a real session answer the assertion cannot tell the two apart - which is
    /// exactly how a mutation disabling the claim hold survived this suite.
    /// </summary>
    private async Task<DirectorUpdateDecision> RunUpdateOwnerAsync(DirectorSupervisor supervisor)
    {
        StageUpdateAndRosters(supervisor);
        return await new DirectorUpdateOwner(supervisor).RunOnceAsync();
    }

    /// <summary>Everything the owner reads, put in place. SEPARATE from running it, so a test can assert
    /// the preconditions its assertion depends on BEFORE the decision is made.</summary>
    private void StageUpdateAndRosters(DirectorSupervisor supervisor)
    {
        var stagedBuild = Path.Combine(_root, "staged", "cc-director.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedBuild)!);
        File.WriteAllText(stagedBuild, "a staged build");

        var stateFile = Path.Combine(InstanceHome, "config", "director", "updater-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        new UpdaterState
        {
            StagedVersion = "9.9.9",
            StagedExecutable = stagedBuild,
            InstallTarget = supervisor.DirectorExePath,
        }.SaveTo(stateFile);

        var journal = Path.Combine(InstanceHome, "config", "director", "crash-journal");
        Directory.CreateDirectory(journal);
        foreach (var file in Directory.GetFiles(InstanceDirectory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            File.WriteAllText(Path.Combine(journal, id + ".json"),
                $$"""{"directorId":"{{id}}","sessions":[]}""");
        }
    }
}
