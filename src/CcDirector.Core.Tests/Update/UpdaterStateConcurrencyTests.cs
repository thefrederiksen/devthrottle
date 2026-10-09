using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// Several processes edit one updater state file (issue #3666, review of #3680).
///
/// The Director writes what its check found, the launcher writes what its install pass decided, and the
/// update helper writes the attempt counter and the health-check marker. Each used to load the whole
/// file, change a field or two, and save the whole file back, with nothing making them take turns: a
/// check that loaded the file, spent a minute downloading and then saved wrote back its stale copy over
/// whatever the launcher had recorded in between. An edit is now one locked step that reads the file as
/// it is, applies only its own change and writes it, and a read never waits on that lock.
/// </summary>
public sealed class UpdaterStateConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "updaterstate-lock-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "updater-state.json");

    public UpdaterStateConcurrencyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// The launcher runs under launchd on macOS and starts the Director in another session. Without the
    /// Global\ prefix a named mutex on Linux and macOS is per session, so the two would hold two different
    /// locks - no lock at all.
    /// </summary>
    [Fact]
    public void TheLock_IsMachineWide_OnEveryPlatform()
    {
        Assert.StartsWith(@"Global\", UpdaterState.LockNameFor(StatePath));
    }

    [Fact]
    public void TheLock_IsTheSameForEverySpellingOfOnePath_AndDifferentForAnotherFile()
    {
        var spelledDifferently = Path.Combine(_dir, ".", "updater-state.json");

        Assert.Equal(UpdaterState.LockNameFor(StatePath), UpdaterState.LockNameFor(spelledDifferently));
        Assert.NotEqual(UpdaterState.LockNameFor(StatePath), UpdaterState.LockNameFor(StatePath + ".other"));
    }

    /// <summary>Two hundred edits at once, each adding one: every one of them must land.</summary>
    [Fact]
    public async Task ConcurrentEdits_LoseNothing()
    {
        UpdaterState.UpdateAt(StatePath, s => s.ApplyAttempts = 0);

        var edits = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => UpdaterState.UpdateAt(StatePath, s => s.ApplyAttempts++)))
            .ToArray();
        await Task.WhenAll(edits);

        Assert.Equal(200, UpdaterState.LoadFrom(StatePath).ApplyAttempts);
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_dir));
    }

    /// <summary>
    /// The reviewer's two-process case: one program read the file, another then edited it, and the first
    /// one's edit must keep the second one's fields.
    /// </summary>
    [Fact]
    public void AnEdit_KeepsWhatAnotherProgramWroteSinceItLastRead()
    {
        UpdaterState.UpdateAt(StatePath, s => { s.StagedVersion = "2.0.0"; s.StagedExecutable = "x"; s.InstallTarget = "y"; });
        var directorsEarlierRead = UpdaterState.LoadFrom(StatePath);

        UpdaterState.UpdateAt(StatePath, s => { s.StagedVersion = null; s.LastApplyDecision = "Applied"; });
        UpdaterState.UpdateAt(StatePath, s => s.LastCheckOutcome = "UpToDate");

        var now = UpdaterState.LoadFrom(StatePath);
        Assert.Equal("2.0.0", directorsEarlierRead.StagedVersion);
        Assert.Null(now.StagedVersion);
        Assert.Equal("Applied", now.LastApplyDecision);
        Assert.Equal("UpToDate", now.LastCheckOutcome);
    }

    /// <summary>
    /// An edit that cannot get the lock must fail loudly and change nothing. It used to be caught and
    /// turned into an empty state, which a later save would have written over the real one.
    /// </summary>
    [Fact]
    public void AnEdit_ThatCannotGetTheLock_Throws_AndLeavesTheFileAlone()
    {
        UpdaterState.UpdateAt(StatePath, s => s.PinnedBadVersion = "2.0.5");

        TimeoutException ex;
        using (new LockHolder(StatePath))
            ex = Assert.Throws<TimeoutException>(() =>
                UpdaterState.UpdateAt(StatePath, s => s.PinnedBadVersion = null, TimeSpan.FromMilliseconds(200)));

        Assert.Contains(StatePath, ex.Message);
        Assert.Equal("2.0.5", UpdaterState.LoadFrom(StatePath).PinnedBadVersion);
    }

    /// <summary>
    /// A read that cannot get the lock THROWS. It used to come back as an empty state, and an empty state
    /// is a statement - "no health check pending" - that startup cleanup acted on by deleting the rollback
    /// backup while the real file said the new build had not yet proved itself.
    /// </summary>
    [Fact]
    public void ARead_ThatCannotGetTheLock_Throws_RatherThanClaimingTheFileIsEmpty()
    {
        UpdaterState.UpdateAt(StatePath, s => s.PendingHealthCheckVersion = "9.9.9");

        using (new LockHolder(StatePath))
            Assert.Throws<TimeoutException>(() => UpdaterState.LoadFrom(StatePath, TimeSpan.FromMilliseconds(200)));

        Assert.Equal("9.9.9", UpdaterState.LoadFrom(StatePath).PendingHealthCheckVersion);
    }

    /// <summary>The three cases where an empty state is the truth: no file yet, zero bytes, and not JSON.</summary>
    [Fact]
    public void ARead_IsEmpty_OnlyWhenTheFileHoldsNothing()
    {
        Assert.Null(UpdaterState.LoadFrom(StatePath).StagedVersion);

        File.WriteAllText(StatePath, "");
        Assert.Null(UpdaterState.LoadFrom(StatePath).StagedVersion);

        File.WriteAllText(StatePath, "{ not json");
        Assert.Null(UpdaterState.LoadFrom(StatePath).StagedVersion);
    }

    /// <summary>
    /// Reads holding the file open must not make an edit's replace fail (Windows refuses to replace an open
    /// file), and an edit's replace must not make a read find no file.
    /// </summary>
    [Fact]
    public async Task ReadsAndEditsAtOnce_AllSucceed()
    {
        // Every read must see the marker; a read that failed comes back as an empty state without it.
        UpdaterState.UpdateAt(StatePath, s => { s.ApplyAttempts = 0; s.PinnedBadVersion = "marker"; });
        var blankReads = 0;

        var work = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => UpdaterState.UpdateAt(StatePath, s => s.ApplyAttempts++)))
            .Concat(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
            {
                if (UpdaterState.LoadFrom(StatePath).PinnedBadVersion != "marker")
                    Interlocked.Increment(ref blankReads);
            })))
            .ToArray();
        await Task.WhenAll(work);

        Assert.Equal(200, UpdaterState.LoadFrom(StatePath).ApplyAttempts);
        Assert.Equal(0, blankReads);
    }

    /// <summary>
    /// A file that is not JSON is replaced by an edit, as a load has always treated it as empty - but a
    /// file that cannot be READ is never overwritten, because nobody knows what it held.
    /// </summary>
    [Fact]
    public void AnEdit_OfAnUnparseableFile_StartsFromEmpty()
    {
        File.WriteAllText(StatePath, "{ not json");

        var written = UpdaterState.UpdateAt(StatePath, s => s.LastCheckOutcome = "UpToDate");

        Assert.Equal("UpToDate", written.LastCheckOutcome);
        Assert.Equal("UpToDate", UpdaterState.LoadFrom(StatePath).LastCheckOutcome);
    }

    /// <summary>Holds the file's lock on a thread of its own, because a mutex is released by the thread that took it.</summary>
    private sealed class LockHolder : IDisposable
    {
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public LockHolder(string path)
        {
            _thread = new Thread(() =>
            {
                using (UpdaterState.AcquireLock(path, TimeSpan.FromSeconds(5)))
                {
                    _held.Set();
                    _release.Wait();
                }
            });
            _thread.Start();
            Assert.True(_held.Wait(TimeSpan.FromSeconds(5)), "the test could not take the lock");
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _held.Dispose();
            _release.Dispose();
        }
    }
}
