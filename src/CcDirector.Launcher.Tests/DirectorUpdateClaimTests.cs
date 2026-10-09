using CcDirector.Core.Update;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The launcher claims a staged update - clears the record - before it stops anything, so no Director
/// that comes up during the swap can hand itself the same update (issue #3666, review of #3680). The
/// swap goes ahead only when the claim was made: a claim that failed used to be logged and ignored,
/// and the swap ran with the record still live.
/// </summary>
public sealed class DirectorUpdateClaimTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-claim-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "updater-state.json");

    public DirectorUpdateClaimTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private StagedDirectorUpdate Staged(string version) => new(version, "build", "target", StatePath);

    [Fact]
    public void TheClaim_ClearsTheRecordItJudged_AndSaysSo()
    {
        UpdaterState.UpdateAt(StatePath, s => { s.StagedVersion = "9.9.9"; s.StagedExecutable = "x"; s.InstallTarget = "y"; });

        Assert.True(DirectorUpdateOwner.ClearStagedRecord(Staged("9.9.9")));
        Assert.Null(UpdaterState.LoadFrom(StatePath).StagedVersion);
    }

    /// <summary>A different build staged since this pass decided is a new record nobody has judged.</summary>
    [Fact]
    public void TheClaim_OfARecordThatNowNamesAnotherVersion_IsNotMade()
    {
        UpdaterState.UpdateAt(StatePath, s => { s.StagedVersion = "9.9.10"; s.StagedExecutable = "x"; s.InstallTarget = "y"; });

        Assert.False(DirectorUpdateOwner.ClearStagedRecord(Staged("9.9.9")));
        Assert.Equal("9.9.10", UpdaterState.LoadFrom(StatePath).StagedVersion);
    }

    /// <summary>
    /// The reviewer's probe: the file's lock held past the wait. The claim must report that it was not
    /// made, so the swap does not run with the record still live.
    /// </summary>
    [Fact]
    public void TheClaim_ThatCannotGetTheLock_IsNotMade()
    {
        UpdaterState.UpdateAt(StatePath, s => { s.StagedVersion = "9.9.9"; s.StagedExecutable = "x"; s.InstallTarget = "y"; });

        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (UpdaterState.AcquireLock(StatePath, TimeSpan.FromSeconds(5)))
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(DirectorUpdateOwner.ClearStagedRecord(Staged("9.9.9")));
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        Assert.Equal("9.9.9", UpdaterState.LoadFrom(StatePath).StagedVersion);
    }
}
