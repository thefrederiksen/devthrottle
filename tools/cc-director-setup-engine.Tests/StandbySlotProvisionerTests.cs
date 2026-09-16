using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The standby Director slot (issue #2945). Two things are faked - an executable's version is read from
/// its file text, and the process list is a field - so the file work (create, replace, never downgrade,
/// carry settings) and the health state reading run for real against a temporary machine root.
/// </summary>
public class StandbySlotProvisionerTests : IDisposable
{
    private readonly string _root;
    private readonly string _primary;
    private readonly string _standby;
    private OtherSlotRunning _running = OtherSlotRunning.No;

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
        Directory.CreateDirectory(Path.GetDirectoryName(_primary) ?? throw new InvalidOperationException("no directory"));
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
            _ => _running,
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

    // ---- the slot path ----

    [Fact]
    public void OtherSlotFor_FromPrimary_ReturnsStandbyFolder()
    {
        Assert.Equal(_standby, StandbySlotProvisioner.OtherSlotFor(_primary));
    }

    [Fact]
    public void OtherSlotFor_FromStandby_ReturnsPrimary()
    {
        Assert.Equal(_primary, StandbySlotProvisioner.OtherSlotFor(_standby));
    }

    [Fact]
    public void OtherSlotFor_StandbyFolderInOtherCase_IsStillTheStandby()
    {
        var upper = Path.Combine(_root, "app", "STANDBY", "cc-director.exe");
        Assert.Equal(_primary, StandbySlotProvisioner.OtherSlotFor(upper));
    }

    // ---- the decision ----

    [Fact]
    public void Decide_NotWindows_ReturnsNotWindows()
    {
        Assert.Equal(StandbySlotDecision.NotWindows,
            StandbySlotProvisioner.Decide(false, HealthGate.Clear, false, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_PendingForThisBuild_HoldsEvenWhenSlotMissing()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseThisBuildIsUnproven,
            StandbySlotProvisioner.Decide(true, HealthGate.PendingForThisBuild, false, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_HealthUnreadable_HoldsEvenWhenSlotMissing()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseHealthStateUnreadable,
            StandbySlotProvisioner.Decide(true, HealthGate.Unreadable, false, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_SlotMissing_Creates()
    {
        Assert.Equal(StandbySlotDecision.Created,
            StandbySlotProvisioner.Decide(true, HealthGate.Clear, false, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_OlderAndRunning_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseOtherSlotIsRunning,
            StandbySlotProvisioner.Decide(true, HealthGate.Clear, true, OtherSlotRunning.Yes, Self, new Version(2, 3, 0)));
    }

    [Fact]
    public void Decide_OlderAndRunningStateUnknown_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseOtherSlotStateUnknown,
            StandbySlotProvisioner.Decide(true, HealthGate.Clear, true, OtherSlotRunning.Unknown, Self, new Version(2, 3, 0)));
    }

    [Fact]
    public void Decide_VersionUnreadable_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseOtherVersionUnreadable,
            StandbySlotProvisioner.Decide(true, HealthGate.Clear, true, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_OlderAndIdle_Replaces()
    {
        Assert.Equal(StandbySlotDecision.Replaced,
            StandbySlotProvisioner.Decide(true, HealthGate.Clear, true, OtherSlotRunning.No, Self, new Version(2, 3, 0)));
    }

    [Fact]
    public void Decide_SameVersion_IsUpToDate()
    {
        Assert.Equal(StandbySlotDecision.UpToDate,
            StandbySlotProvisioner.Decide(true, HealthGate.Clear, true, OtherSlotRunning.No, Self, new Version(2, 4, 0, 0)));
    }

    [Fact]
    public void Decide_OtherNewer_NeverDowngrades()
    {
        Assert.Equal(StandbySlotDecision.UpToDate,
            StandbySlotProvisioner.Decide(true, HealthGate.Clear, true, OtherSlotRunning.No, Self, new Version(2, 5, 0)));
    }

    // ---- the running classification (the production probe hands its process list to this) ----

    private string Exe => _standby;

    [Fact]
    public void ClassifyRunning_NoProcesses_ReturnsNo()
    {
        Assert.Equal(OtherSlotRunning.No, StandbySlotProvisioner.ClassifyRunning([], 1, Exe));
    }

    [Fact]
    public void ClassifyRunning_ProcessFromAnotherPath_ReturnsNo()
    {
        Assert.Equal(OtherSlotRunning.No,
            StandbySlotProvisioner.ClassifyRunning([new ProcessImage(2, _primary)], 1, Exe));
    }

    [Fact]
    public void ClassifyRunning_ProcessFromTheSlot_ReturnsYes()
    {
        Assert.Equal(OtherSlotRunning.Yes,
            StandbySlotProvisioner.ClassifyRunning([new ProcessImage(2, Exe)], 1, Exe));
    }

    [Fact]
    public void ClassifyRunning_PathInOtherCase_ReturnsYes()
    {
        Assert.Equal(OtherSlotRunning.Yes,
            StandbySlotProvisioner.ClassifyRunning([new ProcessImage(2, Exe.ToUpperInvariant())], 1, Exe));
    }

    [Fact]
    public void ClassifyRunning_OnlyThisProcess_IsIgnored()
    {
        Assert.Equal(OtherSlotRunning.No,
            StandbySlotProvisioner.ClassifyRunning([new ProcessImage(1, Exe)], 1, Exe));
    }

    [Fact]
    public void ClassifyRunning_ProcessWouldNotReportImage_ReturnsUnknownNeverNo()
    {
        Assert.Equal(OtherSlotRunning.Unknown,
            StandbySlotProvisioner.ClassifyRunning([new ProcessImage(2, _primary), new ProcessImage(3, null)], 1, Exe));
    }

    [Fact]
    public void ClassifyRunning_UnreportedAndProvenRunning_ReturnsYes()
    {
        Assert.Equal(OtherSlotRunning.Yes,
            StandbySlotProvisioner.ClassifyRunning([new ProcessImage(3, null), new ProcessImage(2, Exe)], 1, Exe));
    }

    // ---- the health gate, against real state files ----

    [Fact]
    public void ReadHealthGate_NoStateFiles_IsClear()
    {
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
    public void ReadHealthGate_MarkerNotAVersion_IsUnreadable()
    {
        Write(InstanceState("default"), "{\"pendingHealthCheckVersion\":\"soon\"}");

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

    // ---- the pass, against real files ----

    [Fact]
    public async Task RunOnceAsync_StandbyMissing_CopiesSelfIntoStandby()
    {
        Write(_primary, "2.4.0");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
        Assert.False(File.Exists(_standby + ".new"), "the staging copy must not be left behind");
    }

    [Fact]
    public async Task RunOnceAsync_StandbyOlder_ReplacesIt()
    {
        Write(_primary, "2.4.0");
        Write(_standby, "2.3.0");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Replaced, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_FromStandbyWithOlderPrimary_ReplacesPrimary()
    {
        Write(_primary, "2.3.0");
        Write(_standby, "2.4.0");

        var outcome = await Provisioner(_standby).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Replaced, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_primary));
    }

    [Fact]
    public async Task RunOnceAsync_StandbyNewer_LeavesItAlone()
    {
        Write(_primary, "2.3.0");
        Write(_standby, "2.4.0");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.UpToDate, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_StandbyRunning_TouchesNothing()
    {
        Write(_primary, "2.4.0");
        Write(_standby, "2.3.0");
        _running = OtherSlotRunning.Yes;

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseOtherSlotIsRunning, outcome.Decision);
        Assert.Equal("2.3.0", File.ReadAllText(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_RunningStateUnknown_TouchesNothing()
    {
        Write(_primary, "2.4.0");
        Write(_standby, "2.3.0");
        _running = OtherSlotRunning.Unknown;

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseOtherSlotStateUnknown, outcome.Decision);
        Assert.Equal("2.3.0", File.ReadAllText(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_AnotherInstanceOwesCheckForThisBuild_CreatesNothing()
    {
        Write(_primary, "2.4.0");
        Write(InstanceState("other"), "{\"pendingHealthCheckVersion\":\"2.4.0\"}");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseThisBuildIsUnproven, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_CorruptHealthState_CreatesNothing()
    {
        Write(_primary, "2.4.0");
        Write(InstanceState("default"), "{ this is not json");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseHealthStateUnreadable, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_NotWindows_CreatesNothing()
    {
        Write(_primary, "2.4.0");

        var outcome = await Provisioner(_primary, isWindows: false).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.NotWindows, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_SwapLockReleasedWhileWaiting_CreatesSlot()
    {
        // The first start after an update: the launcher still holds the swap lock while it waits for this
        // Director to answer. The pass must wait it out, not give up for good.
        Write(_primary, "2.4.0");
        var provisioner = Provisioner(_primary, patience: TimeSpan.FromSeconds(30));
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Held through BinarySwapLock itself, which owns the mutex on one dedicated thread. A raw Mutex held
        // across an await is thread-affine and is exactly the defect that made another test here flaky.
        var holder = BinarySwapLock.RunExclusivelyAsync(
            async () => { holding.SetResult(); await Task.Delay(TimeSpan.FromMilliseconds(700)); return 0; },
            _ => throw new InvalidOperationException("the test could not take its own lock"),
            who: "test holder",
            name: _swapLockName);
        await holding.Task;

        var pass = provisioner.RunOnceAsync();
        await holder;
        var outcome = await pass;

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_SwapLockHeldPastPatience_TouchesNothing()
    {
        Write(_primary, "2.4.0");
        var provisioner = Provisioner(_primary, patience: TimeSpan.FromMilliseconds(200));

        var outcome = await BinarySwapLock.RunExclusivelyAsync(
            () => provisioner.RunOnceAsync(),
            _ => throw new InvalidOperationException("the test could not take its own lock"),
            who: "test holder",
            name: _swapLockName);

        Assert.Equal(StandbySlotDecision.HeldBecauseAnotherSwapIsRunning, outcome.Decision);
        Assert.False(File.Exists(_standby));
    }

    [Fact]
    public async Task RunOnceAsync_Created_CarriesAppSettings()
    {
        Write(_primary, "2.4.0");
        Write(Path.Combine(_root, "app", "appsettings.json"), "{\"primary\":true}");

        await Provisioner(_primary).RunOnceAsync();

        Assert.Equal("{\"primary\":true}", File.ReadAllText(Path.Combine(_root, "app", "standby", "appsettings.json")));
    }

    [Fact]
    public async Task RunOnceAsync_UpToDateButSettingsMissing_CarriesAppSettings()
    {
        Write(_primary, "2.4.0");
        Write(_standby, "2.4.0");
        Write(Path.Combine(_root, "app", "appsettings.json"), "{\"primary\":true}");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.UpToDate, outcome.Decision);
        Assert.Equal("{\"primary\":true}", File.ReadAllText(Path.Combine(_root, "app", "standby", "appsettings.json")));
    }

    [Fact]
    public async Task RunOnceAsync_Replaced_NeverOverwritesExistingAppSettings()
    {
        Write(_primary, "2.4.0");
        Write(_standby, "2.3.0");
        Write(Path.Combine(_root, "app", "appsettings.json"), "{\"primary\":true}");
        Write(Path.Combine(_root, "app", "standby", "appsettings.json"), "{\"standby\":true}");

        await Provisioner(_primary).RunOnceAsync();

        Assert.Equal("{\"standby\":true}", File.ReadAllText(Path.Combine(_root, "app", "standby", "appsettings.json")));
    }
}
