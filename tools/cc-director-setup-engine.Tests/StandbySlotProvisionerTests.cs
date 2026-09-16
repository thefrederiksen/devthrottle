using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The standby Director slot (issue #2945). The machine is faked in two places only - an executable's
/// version is read from its file text, and whether a Director runs from a slot is a field - so the file
/// work itself (create, replace, never downgrade, carry settings) runs for real against a temporary
/// directory.
/// </summary>
public class StandbySlotProvisionerTests : IDisposable
{
    private readonly string _root;
    private readonly string _primary;
    private readonly string _standby;
    private OtherSlotRunning _running = OtherSlotRunning.No;
    private bool _healthCheckPending;

    /// <summary>
    /// This test class's own lock, so it never contends with a real swap on the machine or with another
    /// test project. That production takes the real lock is asserted by BothUpdateOwnersTakeTheSameLockTests.
    /// </summary>
    private readonly string _swapLockName = @"Local\cc-director-binary-swap-test-" + Guid.NewGuid().ToString("N");

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

    private StandbySlotProvisioner Provisioner(string self, bool isWindows = true) =>
        new(self,
            path => Version.TryParse(File.ReadAllText(path), out var v) ? v : null,
            _ => _running,
            () => _healthCheckPending,
            isWindows)
        { SwapLockName = _swapLockName };

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("no directory"));
        File.WriteAllText(path, text);
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

    private static readonly Version Self = new(2, 4, 0);

    [Fact]
    public void Decide_NotWindows_ReturnsNotWindows()
    {
        Assert.Equal(StandbySlotDecision.NotWindows,
            StandbySlotProvisioner.Decide(false, false, false, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_HealthCheckPending_HoldsEvenWhenSlotMissing()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseThisBuildIsUnproven,
            StandbySlotProvisioner.Decide(true, true, false, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_SlotMissing_Creates()
    {
        Assert.Equal(StandbySlotDecision.Created,
            StandbySlotProvisioner.Decide(true, false, false, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_OlderAndRunning_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseOtherSlotIsRunning,
            StandbySlotProvisioner.Decide(true, false, true, OtherSlotRunning.Yes, Self, new Version(2, 3, 0)));
    }

    [Fact]
    public void Decide_OlderAndRunningStateUnknown_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseOtherSlotStateUnknown,
            StandbySlotProvisioner.Decide(true, false, true, OtherSlotRunning.Unknown, Self, new Version(2, 3, 0)));
    }

    [Fact]
    public void Decide_VersionUnreadable_Holds()
    {
        Assert.Equal(StandbySlotDecision.HeldBecauseOtherVersionUnreadable,
            StandbySlotProvisioner.Decide(true, false, true, OtherSlotRunning.No, Self, null));
    }

    [Fact]
    public void Decide_OlderAndIdle_Replaces()
    {
        Assert.Equal(StandbySlotDecision.Replaced,
            StandbySlotProvisioner.Decide(true, false, true, OtherSlotRunning.No, Self, new Version(2, 3, 0)));
    }

    [Fact]
    public void Decide_SameVersion_IsUpToDate()
    {
        Assert.Equal(StandbySlotDecision.UpToDate,
            StandbySlotProvisioner.Decide(true, false, true, OtherSlotRunning.No, Self, new Version(2, 4, 0, 0)));
    }

    [Fact]
    public void Decide_OtherNewer_NeverDowngrades()
    {
        Assert.Equal(StandbySlotDecision.UpToDate,
            StandbySlotProvisioner.Decide(true, false, true, OtherSlotRunning.No, Self, new Version(2, 5, 0)));
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
    public async Task RunOnceAsync_HealthCheckPending_CreatesNothing()
    {
        Write(_primary, "2.4.0");
        _healthCheckPending = true;

        var outcome = await Provisioner(_primary).RunOnceAsync();

        Assert.Equal(StandbySlotDecision.HeldBecauseThisBuildIsUnproven, outcome.Decision);
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
    public async Task RunOnceAsync_SwapLockHeldElsewhere_TouchesNothing()
    {
        Write(_primary, "2.4.0");
        var provisioner = Provisioner(_primary);

        // Hold the lock through BinarySwapLock itself, which owns the mutex on one dedicated thread. A raw
        // Mutex held across an await is thread-affine and is exactly the defect that made another test in
        // this project flaky.
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
