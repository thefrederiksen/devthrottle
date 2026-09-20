using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Indexing;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Background;

/// <summary>How one run of the background scan ended.</summary>
public enum BackgroundScanOutcome
{
    /// <summary>The scan finished, and the scan and its recommendations are saved.</summary>
    Completed,

    /// <summary>The scan ended without a result, and the record says why.</summary>
    Failed,

    /// <summary>The scan was asked to stop before it finished, and the record says so.</summary>
    Stopped,

    /// <summary>
    /// Nothing was done, because a scan was already running. The running scan's record was not
    /// touched.
    /// </summary>
    AnotherScanIsRunning
}

/// <summary>
/// One background scan of one folder: walk it, save what was seen, make the recommendations, save
/// those, and record at every step where the scan stands.
///
/// IT NEVER REMOVES ANYTHING. It scans and it recommends. The mission forbids unattended removal
/// outright, and nothing in this class or in anything it calls can delete, move or change a file
/// outside its own store folder. A person runs the removal, from the command line, later.
///
/// It is written for a scan that takes a long time, because it does: a whole disk was measured at
/// 1,406 seconds. So the record says "running" for all of that time and says since when; the walk can
/// be asked to stop and does so within a folder; and being interrupted is treated as an ordinary
/// event - nothing is written under a name a reader opens until it is whole.
/// </summary>
public sealed class BackgroundScanJob
{
    private readonly string _storeDirectory;
    private readonly IReadOnlyList<IReclaimRule> _rules;

    /// <summary>
    /// Make a job.
    /// </summary>
    /// <param name="storeDirectory">The folder everything is saved into.</param>
    /// <param name="rules">Every rule this machine has.</param>
    public BackgroundScanJob(string storeDirectory, IReadOnlyList<IReclaimRule> rules)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("A store directory cannot be blank.", nameof(storeDirectory));
        _storeDirectory = storeDirectory;
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>
    /// Scan one folder and save the result, unless a scan is already running.
    /// </summary>
    /// <param name="rootPath">The folder to scan.</param>
    /// <param name="stop">Asks the scan to stop.</param>
    public BackgroundScanOutcome Run(string rootPath, CancellationToken stop)
    {
        var root = DirectoryScanner.Canonical(rootPath);
        FileLog.Write($"[BackgroundScanJob] Run: root={root}, store={_storeDirectory}, rules={_rules.Count}");

        using var guard = BackgroundScanGuard.TryAcquire(_storeDirectory);
        if (guard is null)
        {
            FileLog.Write($"[BackgroundScanJob] Run done: root={root}, outcome=AnotherScanIsRunning");
            return BackgroundScanOutcome.AnotherScanIsRunning;
        }

        var previous = BackgroundScanStatusStore.Read(_storeDirectory, root);
        var running = BackgroundScanStatusStore.WriteRunning(_storeDirectory, root, DateTimeOffset.UtcNow, previous);

        // The one place in the engine that catches everything, and it does so to RECORD, not to hide.
        // A scan that threw and recorded nothing would leave a record saying "running" under a process
        // that is still alive, and every reader would wait for ever for a result that is not coming.
        try
        {
            var outcome = ScanAndRecommend(root, running, stop);
            FileLog.Write($"[BackgroundScanJob] Run done: root={root}, outcome={outcome}");
            return outcome;
        }
        catch (OperationCanceledException)
        {
            BackgroundScanStatusStore.WriteFailed(_storeDirectory, running, DateTimeOffset.UtcNow,
                "the scan was asked to stop before it finished, which is what happens when the program " +
                "hosting it shuts down; nothing of the part it had walked was saved");
            FileLog.Write($"[BackgroundScanJob] Run done: root={root}, outcome=Stopped");
            return BackgroundScanOutcome.Stopped;
        }
        catch (Exception ex)
        {
            BackgroundScanStatusStore.WriteFailed(_storeDirectory, running, DateTimeOffset.UtcNow,
                $"the scan threw {ex.GetType().Name}: {ex.Message}");
            FileLog.Write($"[BackgroundScanJob] Run FAILED: root={root}, error={ex}");
            return BackgroundScanOutcome.Failed;
        }
    }

    private BackgroundScanOutcome ScanAndRecommend(string root, BackgroundScanRecord running, CancellationToken stop)
    {
        var scan = new DirectoryScanner().Scan(new ScanOptions { RootPath = root }, stop);
        var scanReport = ScanReportBuilder.Build(scan);

        // A broken instrument is not saved, for the reason the command line tool gives: the saved scan
        // is what a screen shows later, and a measurement just called broken must not be that.
        if (scanReport.Verdict != ReportVerdict.Ok)
        {
            BackgroundScanStatusStore.WriteFailed(_storeDirectory, running, DateTimeOffset.UtcNow,
                $"the scan is a broken instrument and was not saved: {scanReport.BrokenReason}");
            return BackgroundScanOutcome.Failed;
        }

        var indexPath = ScanIndexStore.PathFor(BackgroundScanStatusStore.IndexDirectory(_storeDirectory), root);
        ScanIndexStore.Save(indexPath, scan, DateTimeOffset.UtcNow);

        stop.ThrowIfCancellationRequested();

        var recommendations = RecommendationRun.Against(scan, _rules, DateTimeOffset.UtcNow);
        var recommendationPath = SavedRecommendationStore.PathFor(_storeDirectory, root);
        SavedRecommendationStore.Save(recommendationPath, recommendations, indexPath, DateTimeOffset.UtcNow);

        BackgroundScanStatusStore.WriteCompleted(
            _storeDirectory, running, DateTimeOffset.UtcNow, indexPath, recommendationPath);
        return BackgroundScanOutcome.Completed;
    }
}
