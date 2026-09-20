using CcDirector.Reclaim.Background;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The Launcher's part of the background scan: WHEN a folder is scanned. What a scan found and what
/// its state means are the engine's and are proved in CcDirector.Reclaim.Tests.
///
/// Nothing here scans a real disk. The folders scanned are small trees this class builds in the
/// temporary folder and deletes, and the store each run saves into is one of them too.
/// </summary>
public sealed class BackgroundDiskScanTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cc-launcher-tests", $"background-scan-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private string Tree(string name)
    {
        var root = Path.Combine(_home, name);
        Directory.CreateDirectory(Path.Combine(root, "inner"));
        File.WriteAllBytes(Path.Combine(root, "one.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "inner", "two.bin"), new byte[250]);
        return root;
    }

    private string Store() => Path.Combine(_home, "store");

    private static BackgroundScanStatus Status(
        BackgroundScanState state, DateTimeOffset? started, DateTimeOffset? finished) => new()
    {
        RootPath = "X:\\",
        State = state,
        StartedUtc = started,
        FinishedUtc = finished,
        FailureReason = state == BackgroundScanState.Failed ? "it failed" : null,
        LastCompletedUtc = null,
        IndexPath = null,
        RecommendationPath = null,
        Lines = []
    };

    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsDue_AFolderNeverScanned_IsDue() =>
        Assert.True(BackgroundDiskScan.IsDue(Status(BackgroundScanState.NeverRun, null, null), Now));

    [Fact]
    public void IsDue_AFolderBeingScannedNow_IsNotDue() =>
        Assert.False(BackgroundDiskScan.IsDue(Status(BackgroundScanState.Running, Now.AddHours(-30), null), Now));

    [Fact]
    public void IsDue_AScanFinishedLessThanADayAgo_IsNotDue() =>
        Assert.False(BackgroundDiskScan.IsDue(
            Status(BackgroundScanState.Completed, Now.AddHours(-24), Now.AddHours(-23)), Now));

    [Fact]
    public void IsDue_AScanFinishedADayAgo_IsDue() =>
        Assert.True(BackgroundDiskScan.IsDue(
            Status(BackgroundScanState.Completed, Now.AddHours(-25), Now.AddHours(-24)), Now));

    [Fact]
    public void IsDue_AScanThatFailedAnHourAgo_IsNotThrownStraightBackAtTheDisk() =>
        Assert.False(BackgroundDiskScan.IsDue(
            Status(BackgroundScanState.Failed, Now.AddHours(-2), Now.AddHours(-1)), Now));

    [Fact]
    public void IsDue_AScanThatFailedSixHoursAgo_IsDue() =>
        Assert.True(BackgroundDiskScan.IsDue(
            Status(BackgroundScanState.Failed, Now.AddHours(-7), Now.AddHours(-6)), Now));

    // A scan cut short never recorded an end, so the wait is counted from when it started.
    [Fact]
    public void IsDue_AScanCutShortSixHoursAgo_IsDue() =>
        Assert.True(BackgroundDiskScan.IsDue(Status(BackgroundScanState.Failed, Now.AddHours(-6), null), Now));

    [Fact]
    public async Task RunDueScansAsync_ScansWhatIsDueAndThenLeavesItAlone()
    {
        var first = Tree("first");
        var second = Tree("second");
        var host = new BackgroundDiskScan(Store(), []);

        var outcomes = await host.RunDueScansAsync([first, second], DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, scanned => Assert.Equal(BackgroundScanOutcome.Completed, scanned.Outcome));
        Assert.Equal(BackgroundScanState.Completed, BackgroundScanStatusStore.Read(Store(), first).State);
        Assert.Equal(BackgroundScanState.Completed, BackgroundScanStatusStore.Read(Store(), second).State);

        // The next look, an hour on, finds both fresh and walks nothing.
        var again = await host.RunDueScansAsync(
            [first, second], DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Empty(again);

        // A day on, both are walked again.
        var nextDay = await host.RunDueScansAsync(
            [first, second], DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None);
        Assert.Equal(2, nextDay.Count);
    }

    // The Launcher holds the engine's guard like anything else: a scan already running anywhere on
    // the machine means this look scans nothing and says so.
    [Fact]
    public async Task RunDueScansAsync_WhileAnotherScanHoldsTheGuard_ScansNothing()
    {
        var tree = Tree("guarded");
        var host = new BackgroundDiskScan(Store(), []);

        using (var held = BackgroundScanGuard.TryAcquire(Store()))
        {
            Assert.NotNull(held);
            var outcomes = await host.RunDueScansAsync([tree], DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Equal(BackgroundScanOutcome.AnotherScanIsRunning, Assert.Single(outcomes).Outcome);
            Assert.Equal(BackgroundScanState.NeverRun, BackgroundScanStatusStore.Read(Store(), tree).State);
        }

        var afterwards = await host.RunDueScansAsync([tree], DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(BackgroundScanOutcome.Completed, Assert.Single(afterwards).Outcome);
    }

    [Fact]
    public async Task RunDueScansAsync_NeverRemovesAnything()
    {
        var tree = Tree("untouched");
        var before = Directory.GetFileSystemEntries(tree, "*", SearchOption.AllDirectories)
            .Select(entry => $"{entry} {(File.Exists(entry) ? new FileInfo(entry).Length : -1)}")
            .OrderBy(line => line, StringComparer.Ordinal).ToList();

        await new BackgroundDiskScan(Store(), []).RunDueScansAsync([tree], DateTimeOffset.UtcNow, CancellationToken.None);

        var after = Directory.GetFileSystemEntries(tree, "*", SearchOption.AllDirectories)
            .Select(entry => $"{entry} {(File.Exists(entry) ? new FileInfo(entry).Length : -1)}")
            .OrderBy(line => line, StringComparer.Ordinal).ToList();
        Assert.Equal(3, before.Count);
        Assert.Equal(before, after);
    }

    [Fact]
    public void RootsToScan_NamesOnlyFoldersThatExist()
    {
        var roots = BackgroundDiskScan.RootsToScan();

        Assert.NotEmpty(roots);
        Assert.All(roots, root => Assert.True(Directory.Exists(root), $"{root} is not there"));
    }
}
