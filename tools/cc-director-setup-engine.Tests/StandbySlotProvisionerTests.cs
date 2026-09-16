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
        Assert.Equal(StandbySlotDecision.NotWindows, StandbySlotProvisioner.Decide(false, false, HealthGate.Clear));
    }

    [Fact]
    public void Decide_SlotExists_IsAlreadyPresentWhateverTheHealth()
    {
        Assert.Equal(StandbySlotDecision.AlreadyPresent, StandbySlotProvisioner.Decide(true, true, HealthGate.Unreadable));
    }

    [Fact]
    public void Decide_MissingAndPendingForThisBuild_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseThisBuildIsUnproven,
            StandbySlotProvisioner.Decide(true, false, HealthGate.PendingForThisBuild));
    }

    [Fact]
    public void Decide_MissingAndHealthUnreadable_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseHealthStateUnreadable,
            StandbySlotProvisioner.Decide(true, false, HealthGate.Unreadable));
    }

    [Fact]
    public void Decide_MissingAndClear_Creates()
    {
        Assert.Equal(StandbySlotDecision.Created, StandbySlotProvisioner.Decide(true, false, HealthGate.Clear));
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
        Assert.False(File.Exists(_standby + ".new"), "the staging copy must not be left behind");
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
    public async Task RunOnceAsync_FromStandbyWithPrimaryMissing_CreatesPrimary()
    {
        Write(_standby, "2.4.0");
        File.Delete(_primary);

        var outcome = await Provisioner(_standby).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_primary));
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
    public async Task RunOnceAsync_InterruptedCreation_CompletesWithoutOverwritingSettings()
    {
        // A creation interrupted after the settings were written leaves no executable. The next pass
        // completes it, and keeps the settings that are already there.
        Write(_primarySettings, "{\"primary\":true}");
        Write(_standbySettings, "{\"written before the interruption\":true}");

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.Created, outcome.Decision);
        Assert.Equal("2.4.0", File.ReadAllText(_standby));
        Assert.Equal("{\"written before the interruption\":true}", File.ReadAllText(_standbySettings));
    }

    [Fact]
    public async Task RunOnceAsync_SettingsWriteFails_LeavesNoExecutable()
    {
        // The executable's presence must mean creation finished, so the settings are written first. Make
        // that write fail - a directory stands where the settings file goes - and no executable may appear.
        Write(_primarySettings, "{\"primary\":true}");
        Directory.CreateDirectory(_standbySettings);

        await Assert.ThrowsAnyAsync<Exception>(() => Provisioner(_primary).RunOnceAsync());

        Assert.False(File.Exists(_standby), "an executable without its settings would read as a finished creation");
    }

    // ---- retrying until settled ----

    [Fact]
    public async Task RunUntilSettledAsync_HeldThenCleared_RetriesAndCreates()
    {
        // A health file that cannot be read at one moment must not leave the slot missing until a restart.
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
