using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="CronRunHistoryStore"/> (epic #479, #483): newest-first ordering, the
/// per-job cap, persistence round-trip, and corrupt-file quarantine (the store precedent).
/// </summary>
public sealed class CronRunHistoryStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-cronruns-tests-" + Guid.NewGuid().ToString("N"));

    private string NewPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CronRunRecord Run(string sid, string infra = "started") => new()
    {
        ScheduledUtc = new DateTime(2026, 6, 17, 5, 0, 0, DateTimeKind.Utc),
        FiredUtc = new DateTime(2026, 6, 17, 5, 0, 3, DateTimeKind.Utc),
        TargetDirectorId = "workstation-A",
        SessionId = sid,
        InfraStatus = infra,
        TaskStatus = "unknown",
    };

    [Fact]
    public void Append_ThenList_ReturnsNewestFirst()
    {
        var store = new CronRunHistoryStore(NewPath());
        store.Append("cj_1", Run("sid-a"));
        store.Append("cj_1", Run("sid-b"));

        var runs = store.List("cj_1");
        Assert.Equal(2, runs.Count);
        Assert.Equal("sid-b", runs[0].SessionId); // newest first
        Assert.Equal("sid-a", runs[1].SessionId);
    }

    [Fact]
    public void Append_BeyondCap_PrunesOldest()
    {
        var store = new CronRunHistoryStore(NewPath());
        for (var i = 0; i < CronRunHistoryStore.MaxRecordsPerJob + 10; i++)
            store.Append("cj_1", Run("sid-" + i));

        var runs = store.List("cj_1");
        Assert.Equal(CronRunHistoryStore.MaxRecordsPerJob, runs.Count);
        // newest (highest i) retained, oldest pruned
        Assert.Equal("sid-" + (CronRunHistoryStore.MaxRecordsPerJob + 9), runs[0].SessionId);
    }

    [Fact]
    public void Persistence_RoundTrip_SurvivesReload()
    {
        var path = NewPath();
        var store = new CronRunHistoryStore(path);
        store.Append("cj_1", Run("sid-a"));
        store.Append("cj_2", Run("sid-c"));

        var reloaded = new CronRunHistoryStore(path);
        Assert.Single(reloaded.List("cj_1"));
        Assert.Single(reloaded.List("cj_2"));
        Assert.Equal("sid-a", reloaded.List("cj_1")[0].SessionId);
    }

    [Fact]
    public void List_NoRuns_ReturnsEmpty()
    {
        var store = new CronRunHistoryStore(NewPath());
        Assert.Empty(store.List("cj_unknown"));
    }

    [Fact]
    public void CorruptFile_IsQuarantined_StoreStartsEmpty()
    {
        var path = NewPath();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json !!!");

        var store = new CronRunHistoryStore(path);

        Assert.Empty(store.List("cj_1"));
        Assert.Single(Directory.GetFiles(_dir, Path.GetFileName(path) + ".corrupt-*"));
    }

    [Fact]
    public void Constructor_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CronRunHistoryStore(" "));
    }
}
