using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The standby Director slot (issue #2945). One thing is faked - an executable's version is read from its
/// file text - so the file work and the health state reading run for real against a temporary machine root.
/// </summary>
public class StandbySlotProvisionerTests : IDisposable
{
    private readonly string _root;
    private readonly string _primary;
    private readonly string _standby;
    private readonly string _primarySettings;
    private readonly string _standbySettings;

    /// <summary>
    /// This test class's own lock, so it never contends with a real swap on the machine or with another
    /// test project. That production takes the real lock is asserted by BothUpdateOwnersTakeTheSameLockTests.
    /// </summary>
    private readonly string _swapLockName = @"Local\cc-director-binary-swap-test-" + Guid.NewGuid().ToString("N");

    private static readonly Version Self = new(2, 4, 0);

    public StandbySlotProvisionerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-standby-" + Guid.NewGuid().ToString("N"));
        _primary = Path.Combine(_root, "app", "cc-director.exe");
        _standby = Path.Combine(_root, "app", "standby", "cc-director.exe");
        _primarySettings = Path.Combine(_root, "app", "appsettings.json");
        _standbySettings = Path.Combine(_root, "app", "standby", "appsettings.json");
        Write(_primary, "2.4.0");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private StandbySlotProvisioner Provisioner(string self, bool isWindows = true, TimeSpan? patience = null) =>
        new(self,
            _root,
            path => Version.TryParse(File.ReadAllText(path), out var v) ? v : null,
            isWindows)
        {
            SwapLockName = _swapLockName,
            LockPatience = patience ?? TimeSpan.FromSeconds(5),
        };

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("no directory"));
        File.WriteAllText(path, text);
    }

    private string InstanceState(string slug) => Path.Combine(_root, "instances", slug, "config", "director", "updater-state.json");

    private string RootState() => Path.Combine(_root, "config", "director", "updater-state.json");

    /// <summary>Hold the test lock on BinarySwapLock's own dedicated thread for <paramref name="hold"/>.</summary>
    /// <remarks>
    /// Never a raw Mutex held across an await: a named mutex is thread-affine, and that is exactly the
    /// defect that made another test in this project flaky.
    /// </remarks>
    private async Task<Task> HoldLockAsync(TimeSpan hold)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = BinarySwapLock.RunExclusivelyAsync(
            async () => { holding.SetResult(); await Task.Delay(hold); return 0; },
            _ => throw new InvalidOperationException("the test could not take its own lock"),
            who: "test holder",
            name: _swapLockName);
        await holding.Task;
        return holder;
    }

    // ---- the slot paths ----

    [Fact]
    public void StandbySlotFor_Primary_ReturnsStandbyFolder()
    {
        Assert.Equal(_standby, StandbySlotProvisioner.StandbySlotFor(_primary));
    }

    [Fact]
    public void IsInStandbySlot_PrimaryAndStandby_AreToldApart()
    {
        Assert.False(StandbySlotProvisioner.IsInStandbySlot(_primary));
        Assert.True(StandbySlotProvisioner.IsInStandbySlot(_standby));
        Assert.True(StandbySlotProvisioner.IsInStandbySlot(Path.Combine(_root, "app", "STANDBY", "cc-director.exe")));
    }

    [Fact]
    public void UpdateLeftoversFor_NamesTheUpdateSwappersOwnFiles()
    {
        // Tied to the swapper's own naming, so a renamed staging or backup suffix cannot silently slip past.
        var leftovers = StandbySlotProvisioner.UpdateLeftoversFor(_standby);

        Assert.Contains(DirectorBuildSwapper.StagingPathFor(_standby), leftovers);
        Assert.Contains(DirectorBuildSwapper.BackupPathFor(_standby), leftovers);
        Assert.Contains(DirectorBuildSwapper.BackupPathFor(_standby, DirectorBuildSwapper.LauncherBackupSuffix), leftovers);
        Assert.DoesNotContain(_standby + StandbySlotProvisioner.StagingSuffix, leftovers);
    }

    // ---- the decision ----

    [Fact]
    public void Decide_NotWindows_ReturnsNotWindows()
    {
        Assert.Equal(StandbySlotDecision.NotWindows, StandbySlotProvisioner.Decide(false, true, false, false, HealthGate.Clear));
    }

    [Fact]
    public void Decide_NotPrimary_NeverCreates()
    {
        Assert.Equal(StandbySlotDecision.NotPrimary, StandbySlotProvisioner.Decide(true, false, false, false, HealthGate.Clear));
    }

    [Fact]
    public void Decide_StandbyExists_IsAlreadyPresentWhateverElse()
    {
        Assert.Equal(StandbySlotDecision.AlreadyPresent, StandbySlotProvisioner.Decide(true, true, true, true, HealthGate.Unreadable));
    }

    [Fact]
    public void Decide_MissingWithUpdateLeftover_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseAnUpdateOwnsTheSlot,
            StandbySlotProvisioner.Decide(true, true, false, true, HealthGate.Clear));
    }

    [Fact]
    public void Decide_MissingAndPendingForThisBuild_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseThisBuildIsUnproven,
            StandbySlotProvisioner.Decide(true, true, false, false, HealthGate.PendingForThisBuild));
    }

    [Fact]
    public void Decide_MissingAndHealthUnreadable_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseHealthStateUnreadable,
            StandbySlotProvisioner.Decide(true, true, false, false, HealthGate.Unreadable));
    }

    [Fact]
    public void Decide_MissingAndClear_Creates()
    {
        Assert.Equal(StandbySlotDecision.Created, StandbySlotProvisioner.Decide(true, true, false, false, HealthGate.Clear));
    }

    // ---- the health gate, against real state files ----

    [Fact]
    public void ReadHealthGate_NoStateFiles_IsClear()
    {
        Assert.Equal(HealthGate.Clear, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_InstanceWithoutStateFile_IsClear()
    {
        Directory.CreateDirectory(Path.Combine(_root, "instances", "fresh"));

        Assert.Equal(HealthGate.Clear, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_StaleMarkerForAnotherVersion_IsClear()
    {
        // A real machine carried a pending marker for 1.8.0 at its shared root for months. A marker for
        // another build must not hold this one, or that machine would never get its standby slot.
        Write(RootState(), "{\"pendingHealthCheckVersion\":\"1.8.0\"}");

        Assert.Equal(HealthGate.Clear, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_ExplicitNullMarker_IsClear()
    {
        Write(InstanceState("default"), "{\"pendingHealthCheckVersion\":null}");

        Assert.Equal(HealthGate.Clear, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_AnotherInstanceOwesCheckForThisBuild_IsPending()
    {
        Write(InstanceState("default"), "{}");
        Write(InstanceState("other"), "{\"pendingHealthCheckVersion\":\"2.4.0\"}");

        Assert.Equal(HealthGate.PendingForThisBuild, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_CorruptStateFile_IsUnreadableNeverClear()
    {
        Write(InstanceState("default"), "{ this is not json");

        Assert.Equal(HealthGate.Unreadable, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_StatePathThatCannotBeReadAsAFile_IsUnreadableNeverClear()
    {
        // File.Exists answers false for a path it cannot read as a file - here a directory stands where the
        // state file goes. Asking existence first would skip it as absent; reading it must hold instead.
        Directory.CreateDirectory(InstanceState("default"));

        Assert.Equal(HealthGate.Unreadable, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_InstancesThatCannotBeListed_IsUnreadableNeverClear()
    {
        // A file where the instances directory goes: the named instances cannot be listed at all.
        Write(Path.Combine(_root, "instances"), "not a directory");

        Assert.Equal(HealthGate.Unreadable, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_MarkerNotAVersion_IsUnreadable()
    {
        Write(InstanceState("default"), "{\"pendingHealthCheckVersion\":\"soon\"}");

        Assert.Equal(HealthGate.Unreadable, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Theory]
    [InlineData("240")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[\"2.4.0\"]")]
    public void ReadHealthGate_MarkerOfWrongType_IsUnreadableNeverClear(string markerJson)
    {
        Write(InstanceState("default"), "{\"pendingHealthCheckVersion\":" + markerJson + "}");

        Assert.Equal(HealthGate.Unreadable, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    [Fact]
    public void ReadHealthGate_MarkerWrittenByRealUpdaterState_IsRead()
    {
        // Ties the gate to the product's own serializer: if UpdaterState ever renames its JSON property,
        // this fails instead of the gate silently reading every check as clear.
        var path = InstanceState("default");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("no directory"));
        new UpdaterState { PendingHealthCheckVersion = "2.4.0" }.SaveTo(path);

        Assert.Equal(HealthGate.PendingForThisBuild, StandbySlotProvisioner.ReadHealthGate(_root, Self));
    }

    // ---- one pass, against real files ----

    [Fact]
    public async Task RunOnceAsync_StandbyMissing_CreatesItFromSelf()
    {
        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
        Assert.False(File.Exists(_standby + StandbySlotProvisioner.StagingSuffix), "the staging copy must not be left behind");
    }

    [Fact]
    public async Task RunOnceAsync_StandbyExistsAndOlder_IsNeverTouched()
    {
        // Only ever creates. A Director keeps itself up to date; replacing its file here would race that.
        Write(_standby, "2.3.0");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.AlreadyPresent, outcome.Decision);
        Assert.Equal("2.3.0", File.ReadAllText(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_FromStandbyWithPrimaryMissing_NeverWritesThePrimary()
    {
        // The installer, launcher and updater own the primary - including recovering one that went missing
        // mid-update. The standby never recreates it.
        Write(_standby, "2.4.0");
        File.Delete(_primary);

        var outcome = await Provisioner(_standby).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.NotPrimary, outcome.Decision);
        Assert.True(outcome.IsSettled);
        Assert.False(File.Exists(_primary));
    }

    [Theory]
    [InlineData(".new")]
    [InlineData(".old")]
    [InlineData(".prev")]
    public async Task RunOnceAsync_MissingStandbyWithUpdateLeftover_CreatesNothing(string suffix)
    {
        // An update interrupted halfway: the executable is gone, its staging or backup file remains, and its
        // own recovery must put it back. Creating the slot here would defeat that recovery.
        Write(_standby + suffix, "2.5.0");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseAnUpdateOwnsTheSlot, outcome.Decision);
        Assert.False(File.Exists(_standby));
        Assert.Equal("2.5.0", File.ReadAllText(_standby + suffix));
    }

    [Fact]
    public async Task RunOnceAsync_UpdateLeftoverThatFileExistsCannotSee_StillHolds()
    {
        // File.Exists answers false for something it cannot see as a plain file - here a directory named
        // like the backup. The leftover check must never ask it, or the slot is created over that update.
        Directory.CreateDirectory(_standby + ".old");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseAnUpdateOwnsTheSlot, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public void MayBePresent_NothingThere_IsFalse_AndSomethingThere_IsTrue()
    {
        Assert.False(StandbySlotProvisioner.MayBePresent(_standby));
        Assert.False(StandbySlotProvisioner.MayBePresent(Path.Combine(_root, "no-such-folder", "cc-director.exe")));
        Assert.True(StandbySlotProvisioner.MayBePresent(_primary));
        Assert.True(StandbySlotProvisioner.MayBePresent(Path.Combine(_root, "app")));
    }

    [Fact]
    public async Task RunOnceAsync_OwnStagingLeftoverFromACrash_IsReplacedAndCleaned()
    {
        // A pass killed mid-copy leaves a partial file under its own staging name. That is not an update's
        // file, so it must not hold; the next pass overwrites it and cleans up.
        Write(_standby + StandbySlotProvisioner.StagingSuffix, "2.4");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
        Assert.False(File.Exists(_standby + StandbySlotProvisioner.StagingSuffix));
    }

    [Fact]
    public async Task RunOnceAsync_AnotherInstanceOwesCheckForThisBuild_CreatesNothing()
    {
        Write(InstanceState("other"), "{\"pendingHealthCheckVersion\":\"2.4.0\"}");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseThisBuildIsUnproven, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_CorruptHealthState_CreatesNothing()
    {
        Write(InstanceState("default"), "{ this is not json");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseHealthStateUnreadable, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_NotWindows_CreatesNothing()
    {
        var outcome = await Provisioner(_primary, isWindows: false).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.NotWindows, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_SwapLockReleasedWhileWaiting_CreatesSlot()
    {
        // The first start after an update: the launcher still holds the swap lock while it waits for this
        // Director to answer. The pass must wait it out, not give up.
        var holder = await HoldLockAsync(TimeSpan.FromMilliseconds(700));

        var pass = Provisioner(_primary, patience: TimeSpan.FromSeconds(30)).RunOnceAsync();
        await holder;
        var outcome = await pass;

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_SwapLockHeldPastPatience_CreatesNothing()
    {
        var holder = await HoldLockAsync(TimeSpan.FromSeconds(2));

        var outcome = await Provisioner(_primary, patience: TimeSpan.FromMilliseconds(200)).RunOnceAsync();
        await holder;

        Assert.Equal(StandbySlotDecision.HeldBecauseAnotherSwapIsRunning, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_Created_CarriesAppSettings()
    {
        Write(_primarySettings, "{\"primary\":true}");

        await Provisioner(_primary).RunOnceAsync();

        Assert.Equal("{\"primary\":true}", File.ReadAllText(_standbySettings));
    }

    [Fact]
    public async Task RunOnceAsync_PartialSettingsStagingFromACrash_CarriesTheWholeFile()
    {
        // A copy killed mid-write leaves only a partial file under the staging name, never under the real
        // one, so the next pass still carries the whole settings document.
        Write(_primarySettings, "{\"primary\":true}");
        Write(_standbySettings + StandbySlotProvisioner.StagingSuffix, "{\"prim");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("{\"primary\":true}", File.ReadAllText(_standbySettings));
        Assert.False(File.Exists(_standbySettings + StandbySlotProvisioner.StagingSuffix));
    }

    [Fact]
    public async Task RunOnceAsync_InterruptedCreation_CompletesWithoutOverwritingSettings()
    {
        // A creation interrupted after the settings were placed leaves no executable. The next pass
        // completes it, and keeps the settings that are already there.
        Write(_primarySettings, "{\"primary\":true}");
        Write(_standbySettings, "{\"placed before the interruption\":true}");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
        Assert.Equal("{\"placed before the interruption\":true}", File.ReadAllText(_standbySettings));
    }

    [Fact]
    public async Task RunOnceAsync_SettingsWriteFails_LeavesNoExecutable()
    {
        // The executable's presence must mean creation finished, so the settings are placed first. Make
        // that fail - a directory stands where the settings staging file goes - and no executable may appear.
        Write(_primarySettings, "{\"primary\":true}");
        Directory.CreateDirectory(_standbySettings + StandbySlotProvisioner.StagingSuffix);

        await Assert.ThrowsAnyAsync<Exception>(() => Provisioner(_primary).RunOnceAsync());

        Assert.False(File.Exists(_standby), "an executable without its settings would read as a finished creation");
    }

    // ---- retrying until settled ----

    [Fact]
    public async Task RunUntilSettledAsync_HeldThenCleared_RetriesAndCreates()
    {
        // Health state that cannot be read at one moment must not leave the slot missing until a restart.
        var corrupt = InstanceState("default");
        Write(corrupt, "{ this is not json");
        var delays = 0;

        var outcome = await Provisioner(_primary).RunUntilSettledAsync(_ =>
        {
            delays++;
            File.WriteAllText(corrupt, "{}");   // the writer finishes while this pass waits
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(1, delays);
        Assert.NotNull(outcome);
        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.True(File.Exists(_standby));
    }

    [Fact]
    public async Task RunUntilSettledAsync_AlreadyPresent_SettlesWithoutWaiting()
    {
        Write(_standby, "2.4.0");
        var delays = 0;

        var outcome = await Provisioner(_primary).RunUntilSettledAsync(_ => { delays++; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(0, delays);
        Assert.NotNull(outcome);
        Assert.Equal(StandbySlotDecision.AlreadyPresent, outcome.Decision);
    }

    [Fact]
    public async Task RunUntilSettledAsync_CancelledWhileHeld_ReturnsNullAndCreatesNothing()
    {
        Write(InstanceState("default"), "{ this is not json");
        using var cts = new CancellationTokenSource();

        var outcome = await Provisioner(_primary).RunUntilSettledAsync(ct =>
        {
            cts.Cancel();
            return Task.FromCanceled(ct);
        }, cts.Token);

        Assert.Null(outcome);
        Assert.False(File.Exists(_standby));
    }
}
