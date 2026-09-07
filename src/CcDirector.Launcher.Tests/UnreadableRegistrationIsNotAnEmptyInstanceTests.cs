using System.ComponentModel;
using CcDirector.Core.Instances;
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
        Assert.Contains("names no Director", string.Join(" ", lookup.Unreadable));
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
    /// THE LIVE DEFECT, ASSERTED ON THE REAL START PATH.
    ///
    /// HOW THIS OBSERVES A START WITHOUT STARTING ANYTHING. The installed Director is a file named
    /// <c>cc-director.exe</c> whose contents are text. <c>Start</c> checks that the file EXISTS - it
    /// does - and then, if it decides to go ahead, hands it to ShellExecute, which refuses it as not a
    /// valid application and throws. So a refusal to start is an ordinary return, and an ATTEMPT to start
    /// is an exception. Nothing is ever launched.
    ///
    /// Read this test together with <see cref="An_empty_instance_home_lets_Start_attempt_a_start"/>. On
    /// its own, a pass condition of "no exception was thrown" is an ABSENCE, and an absence certifies a
    /// run that never happened - if the rig could not have thrown, this would pass against the defect it
    /// exists to catch. Its partner proves the rig throws when a start is attempted, which is what turns
    /// this into evidence.
    /// </summary>
    [Fact]
    public void One_corrupt_registration_does_not_let_Start_put_a_second_Director_on_a_live_instance_home()
    {
        // Windows-only premise: off Windows the installed Director is the machine-global
        // ~/Applications/Director.app, so this rig cannot control whether the exe-exists guard passes.
        if (!OperatingSystem.IsWindows()) return;

        var layout = FakeInstalledDirector();
        WriteTruncatedRegistration();
        var supervisor = new DirectorSupervisor(layout, Locator());

        // No throw = it refused to start. See the partner test for why that is a real assertion.
        supervisor.Start();
    }

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
}
