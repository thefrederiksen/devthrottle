using CcDirector.Reclaim.Background;
using CcDirector.Reclaim.Indexing;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Scanning;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The background scan the Launcher hosts, proved on fixture trees.
///
/// Every test here runs the real job over a real tree and then asks the real readers what they find,
/// because the readers are what a Director will use. Nothing here hands a reader a record typed by
/// the test, except where the thing under test IS a record left behind by a process that died, which
/// no test can produce honestly by dying.
/// </summary>
public sealed class BackgroundScanJobTests
{
    // A rule that looks inside the fixture tree, so the job really runs a rule and really folds it.
    // It can be told to wait inside Examine, which is how a test holds one scan open while it tries
    // to start another, and to throw, which is how a test makes a scan fail part way.
    private sealed class FixtureRule(string looksIn) : IReclaimRule
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim? WaitFor { get; init; }
        public Exception? Throw { get; init; }

        public string Id => "fixture-rule";
        public string Name => "A rule the tests own";
        public ProofKind Proof => ProofKind.WeMadeIt;
        public string WhatItRemoves => "nothing; it exists to be run";
        public string WhyItIsSafe => "it offers nothing";
        public string WhatIsLost => "nothing";
        public string HowToGetItBack => "there is nothing to get back";
        public int AgeGateDays => 7;
        public bool NeedsAdministrator => false;
        public string? CommandToRun => null;
        public string LooksIn => looksIn;

        public RuleAnswer Examine(RuleContext context)
        {
            Entered.Set();
            WaitFor?.Wait(TimeSpan.FromSeconds(60));
            if (Throw is not null) throw Throw;
            return new RuleAnswer([new RuleControl("folders-examined", 1, MustNotBeEmpty: true)], []);
        }
    }

    [Fact]
    public void Run_OverAFixtureTree_SavesTheScanAndTheRecommendationsAndRecordsCompleted()
    {
        using var tree = StandardFixture.Build("background-completes");
        using var store = new FixtureTree("background-completes-store");
        var job = new BackgroundScanJob(store.Root, [new FixtureRule(tree.Root)]);

        var outcome = job.Run(tree.Root, CancellationToken.None);

        Assert.Equal(BackgroundScanOutcome.Completed, outcome);

        var status = BackgroundScanStatusStore.Read(store.Root, tree.Root);
        Assert.Equal(BackgroundScanState.Completed, status.State);
        Assert.NotNull(status.IndexPath);
        Assert.NotNull(status.RecommendationPath);
        Assert.Equal(status.FinishedUtc, status.LastCompletedUtc);

        var index = ScanIndexStore.Load(status.IndexPath);
        Assert.Equal(StandardFixture.ExpectedBytesSeen, index.Scan.BytesSeen);
        Assert.Equal(StandardFixture.ExpectedFilesSeen, index.Scan.FilesSeen);

        var saved = SavedRecommendationStore.Load(status.RecommendationPath);
        Assert.Equal("ok", saved.Verdict);
        Assert.Equal(1, saved.RulesRun);
        Assert.Contains("rule: fixture-rule", saved.Lines);
        Assert.Equal(status.IndexPath, saved.IndexPath);
    }

    [Fact]
    public void Run_NeverRemovesAnything_TheTreeIsByteForByteWhatItWas()
    {
        using var tree = StandardFixture.Build("background-removes-nothing");
        using var store = new FixtureTree("background-removes-nothing-store");
        var before = Snapshot(tree.Root);

        var outcome = new BackgroundScanJob(store.Root, [new FixtureRule(tree.Root)])
            .Run(tree.Root, CancellationToken.None);

        Assert.Equal(BackgroundScanOutcome.Completed, outcome);
        Assert.NotEmpty(before);
        Assert.Equal(before, Snapshot(tree.Root));
    }

    // THE GUARD. While one scan runs, a second is refused: it scans nothing, saves nothing, and
    // leaves the running scan's record alone.
    [Fact]
    public async Task Run_WhileAnotherScanIsRunning_IsRefusedAndTouchesNothing()
    {
        using var firstTree = StandardFixture.Build("guard-first");
        using var secondTree = StandardFixture.Build("guard-second");
        using var store = new FixtureTree("guard-store");

        using var release = new ManualResetEventSlim(false);
        var holding = new FixtureRule(firstTree.Root) { WaitFor = release };
        var first = Task.Run(() =>
            new BackgroundScanJob(store.Root, [holding]).Run(firstTree.Root, CancellationToken.None));

        try
        {
            Assert.True(holding.Entered.Wait(TimeSpan.FromSeconds(30)), "the first scan never reached its rule");

            // The first scan is now inside the guard, with its record saying running.
            Assert.Equal(BackgroundScanState.Running, BackgroundScanStatusStore.Read(store.Root, firstTree.Root).State);

            var second = new BackgroundScanJob(store.Root, [new FixtureRule(secondTree.Root)])
                .Run(secondTree.Root, CancellationToken.None);

            Assert.Equal(BackgroundScanOutcome.AnotherScanIsRunning, second);
            Assert.Equal(BackgroundScanState.NeverRun, BackgroundScanStatusStore.Read(store.Root, secondTree.Root).State);
            Assert.False(File.Exists(ScanIndexStore.PathFor(BackgroundScanStatusStore.IndexDirectory(store.Root), secondTree.Root)));
            Assert.Equal(BackgroundScanState.Running, BackgroundScanStatusStore.Read(store.Root, firstTree.Root).State);
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(BackgroundScanOutcome.Completed, await first);

        // And the guard lets go: once the first has ended, the second folder scans.
        var afterwards = new BackgroundScanJob(store.Root, [new FixtureRule(secondTree.Root)])
            .Run(secondTree.Root, CancellationToken.None);
        Assert.Equal(BackgroundScanOutcome.Completed, afterwards);
    }

    // STATE ONE OF THREE: never run.
    [Fact]
    public void Read_WhenNoScanWasEverStarted_SaysNeverRunAndSaysNothingWasLost()
    {
        using var tree = StandardFixture.Build("state-never-run");
        using var store = new FixtureTree("state-never-run-store");

        var status = BackgroundScanStatusStore.Read(store.Root, tree.Root);

        Assert.Equal(BackgroundScanState.NeverRun, status.State);
        Assert.Null(status.FailureReason);
        Assert.Null(status.StartedUtc);
        Assert.Contains("background-scan: never-run", status.Lines);
        Assert.DoesNotContain(status.Lines, line => line.StartsWith("reason:", StringComparison.Ordinal));
    }

    // STATE TWO OF THREE: running, read from outside while the scan really is running.
    [Fact]
    public async Task Read_WhileAScanIsRunning_SaysRunningAndSinceWhen()
    {
        using var tree = StandardFixture.Build("state-running");
        using var store = new FixtureTree("state-running-store");

        using var release = new ManualResetEventSlim(false);
        var holding = new FixtureRule(tree.Root) { WaitFor = release };
        var run = Task.Run(() =>
            new BackgroundScanJob(store.Root, [holding]).Run(tree.Root, CancellationToken.None));

        try
        {
            Assert.True(holding.Entered.Wait(TimeSpan.FromSeconds(30)), "the scan never reached its rule");

            var status = BackgroundScanStatusStore.Read(store.Root, tree.Root);

            Assert.Equal(BackgroundScanState.Running, status.State);
            Assert.NotNull(status.StartedUtc);
            Assert.Null(status.FinishedUtc);
            Assert.Null(status.FailureReason);
            Assert.Contains("background-scan: running", status.Lines);
            Assert.Contains("last-result: none, because no scan of this folder has ever finished", status.Lines);
        }
        finally
        {
            release.Set();
        }

        await run;
    }

    // STATE THREE OF THREE: failed, because the scan threw. It must not read as never run, and it
    // must not read as running.
    [Fact]
    public void Read_AfterAScanThrew_SaysFailedAndSaysWhy()
    {
        using var tree = StandardFixture.Build("state-failed");
        using var store = new FixtureTree("state-failed-store");
        var throwing = new FixtureRule(tree.Root) { Throw = new InvalidOperationException("the rule fell over") };

        var outcome = new BackgroundScanJob(store.Root, [throwing]).Run(tree.Root, CancellationToken.None);

        Assert.Equal(BackgroundScanOutcome.Failed, outcome);
        var status = BackgroundScanStatusStore.Read(store.Root, tree.Root);
        Assert.Equal(BackgroundScanState.Failed, status.State);
        Assert.NotNull(status.FinishedUtc);
        Assert.Contains("the rule fell over", status.FailureReason);
        Assert.Contains("background-scan: failed", status.Lines);
        Assert.Contains(status.Lines, line => line.StartsWith("reason:", StringComparison.Ordinal));
    }

    // Failed, the other way: the folder is not there. Nothing is walked, and the record says so.
    [Fact]
    public void Read_AfterAScanOfAFolderThatIsNotThere_SaysFailed()
    {
        using var store = new FixtureTree("state-failed-missing-store");
        var missing = Path.Combine(store.Root, "there-is-no-such-folder");

        var outcome = new BackgroundScanJob(store.Root, []).Run(missing, CancellationToken.None);

        Assert.Equal(BackgroundScanOutcome.Failed, outcome);
        var status = BackgroundScanStatusStore.Read(store.Root, missing);
        Assert.Equal(BackgroundScanState.Failed, status.State);
        Assert.Contains("DirectoryNotFoundException", status.FailureReason);
    }

    // The case the mandate names: a record that says running, left by a process that is gone, is a
    // scan that FAILED. The record is written by the real job; what the test changes is who it says
    // wrote it, because a test cannot die and then read what it left.
    [Fact]
    public async Task Read_WhenTheRecordSaysRunningAndItsProcessIsGone_SaysFailedNotRunning()
    {
        using var tree = StandardFixture.Build("state-cut-short");
        using var store = new FixtureTree("state-cut-short-store");

        using var release = new ManualResetEventSlim(false);
        var holding = new FixtureRule(tree.Root) { WaitFor = release };
        var run = Task.Run(() =>
            new BackgroundScanJob(store.Root, [holding]).Run(tree.Root, CancellationToken.None));

        string recordText;
        var recordPath = BackgroundScanStatusStore.PathFor(store.Root, tree.Root);
        try
        {
            Assert.True(holding.Entered.Wait(TimeSpan.FromSeconds(30)), "the scan never reached its rule");
            recordText = File.ReadAllText(recordPath);
        }
        finally
        {
            release.Set();
        }

        await run;

        // Put the running record back as a dead process would have left it: same record, but the
        // process it names started at another time, which is what a reused process number looks like.
        var own = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"\"holderProcessId\": {own}", recordText);
        var holderLine = recordText.Split('\n').Single(line => line.Contains("\"holderStartedUtc\"", StringComparison.Ordinal));
        File.WriteAllText(recordPath, recordText.Replace(
            holderLine, "  \"holderStartedUtc\": \"2001-01-01T00:00:00+00:00\",", StringComparison.Ordinal));

        var status = BackgroundScanStatusStore.Read(store.Root, tree.Root);

        Assert.Equal(BackgroundScanState.Failed, status.State);
        Assert.Contains("cut short", status.FailureReason);
        Assert.Contains("background-scan: failed", status.Lines);
    }

    // A record that cannot be read is failed, never "never run": something was written there.
    [Fact]
    public void Read_WhenTheRecordCannotBeRead_SaysFailedNotNeverRun()
    {
        using var tree = StandardFixture.Build("state-unreadable");
        using var store = new FixtureTree("state-unreadable-store");
        Assert.Equal(BackgroundScanOutcome.Completed,
            new BackgroundScanJob(store.Root, [new FixtureRule(tree.Root)]).Run(tree.Root, CancellationToken.None));

        File.WriteAllText(BackgroundScanStatusStore.PathFor(store.Root, tree.Root), "{ this is not a record");

        var status = BackgroundScanStatusStore.Read(store.Root, tree.Root);

        Assert.Equal(BackgroundScanState.Failed, status.State);
        Assert.Contains("not a background scan record", status.FailureReason);
    }

    // A scan that fails after one that finished keeps pointing at the result that is still there.
    [Fact]
    public void Read_AfterAFailureThatFollowsASuccess_StillNamesTheLastWholeResult()
    {
        using var tree = StandardFixture.Build("state-failed-after-success");
        using var store = new FixtureTree("state-failed-after-success-store");
        new BackgroundScanJob(store.Root, [new FixtureRule(tree.Root)]).Run(tree.Root, CancellationToken.None);
        var completed = BackgroundScanStatusStore.Read(store.Root, tree.Root);

        new BackgroundScanJob(store.Root, [new FixtureRule(tree.Root) { Throw = new IOException("no") }])
            .Run(tree.Root, CancellationToken.None);

        var status = BackgroundScanStatusStore.Read(store.Root, tree.Root);
        Assert.Equal(BackgroundScanState.Failed, status.State);
        Assert.Equal(completed.LastCompletedUtc, status.LastCompletedUtc);
        Assert.Equal(completed.RecommendationPath, status.RecommendationPath);
        Assert.Contains(status.Lines, line => line.StartsWith("last-result: the scan that finished at", StringComparison.Ordinal));
    }

    // INTERRUPTED, THE FIRST WAY: asked to stop during the walk. Nothing is saved, the record says
    // stopped, and the result of the scan before it is still the one a reader gets.
    [Fact]
    public void Run_AskedToStopDuringTheWalk_SavesNothingAndKeepsTheEarlierResult()
    {
        using var tree = StandardFixture.Build("stopped");
        using var store = new FixtureTree("stopped-store");
        new BackgroundScanJob(store.Root, [new FixtureRule(tree.Root)]).Run(tree.Root, CancellationToken.None);
        var indexPath = ScanIndexStore.PathFor(BackgroundScanStatusStore.IndexDirectory(store.Root), tree.Root);
        var indexBefore = File.ReadAllText(indexPath);

        // The tree grows, so a scan that was wrongly saved would be visibly different.
        tree.File("added-later.bin", 7777);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var outcome = new BackgroundScanJob(store.Root, [new FixtureRule(tree.Root)]).Run(tree.Root, stop.Token);

        Assert.Equal(BackgroundScanOutcome.Stopped, outcome);
        Assert.Equal(indexBefore, File.ReadAllText(indexPath));
        Assert.Equal(StandardFixture.ExpectedBytesSeen, ScanIndexStore.Load(indexPath).Scan.BytesSeen);

        var status = BackgroundScanStatusStore.Read(store.Root, tree.Root);
        Assert.Equal(BackgroundScanState.Failed, status.State);
        Assert.Contains("asked to stop", status.FailureReason);
    }

    [Fact]
    public void Scan_AskedToStop_ThrowsRatherThanReturningPartOfADisk()
    {
        using var tree = StandardFixture.Build("scanner-stopped");
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new DirectoryScanner().Scan(new ScanOptions { RootPath = tree.Root }, stop.Token));
    }

    private static List<string> Snapshot(string root)
    {
        // Every entry that can be reached, with its size. The refused folder refuses the test too,
        // which is the point of it, so it is recorded by name and not walked.
        var lines = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(folder);
            }
            catch (UnauthorizedAccessException)
            {
                lines.Add($"refused {folder}");
                continue;
            }

            foreach (var entry in entries)
            {
                var info = new FileInfo(entry);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) lines.Add($"link {entry}");
                else if ((info.Attributes & FileAttributes.Directory) != 0) { lines.Add($"folder {entry}"); pending.Push(entry); }
                else lines.Add($"file {entry} {info.Length} {info.LastWriteTimeUtc.Ticks}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }
}
