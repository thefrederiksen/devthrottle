using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="CronRunHistoryStore"/> (epic #479, #483) over the EF data layer (Hosted Gateway
/// mission, Step 1b): newest-first ordering, the per-job cap, the persistence round-trip, and the one-time
/// legacy-JSON import (lossless newest-first + cap preserved, fail-loud).
/// </summary>
public sealed class CronRunHistoryStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string LegacyPath() => _h.LegacyPath("cronruns-" + Guid.NewGuid().ToString("N") + ".json");

    private static CronRunRecord Run(string sid, string infra = "started") => new()
    {
        ScheduledUtc = new DateTime(2026, 6, 17, 5, 0, 0, DateTimeKind.Utc),
        FiredUtc = new DateTime(2026, 6, 17, 5, 0, 3, DateTimeKind.Utc),
        Machine = "workstation-A",
        TargetDirectorId = "director-1",
        SessionId = sid,
        InfraStatus = infra,
        TaskStatus = "unknown",
    };

    [Fact]
    public void Append_ThenList_ReturnsNewestFirst()
    {
        var store = new CronRunHistoryStore(_h.Open(), LegacyPath());
        store.Append("cj_1", Run("sid-a"));
        store.Append("cj_1", Run("sid-b"));

        var runs = store.List("cj_1");
        Assert.Equal(2, runs.Count);
        Assert.Equal("sid-b", runs[0].SessionId); // newest first
        Assert.Equal("sid-a", runs[1].SessionId);
    }

    /// <summary>
    /// A RECORDED FIRE NAMES THE SESSION IT STARTED, AND THAT IS HOW THE WINGMAN TAB KNOWS A SCHEDULE ASKED.
    ///
    /// The Now view's working state says who asked a session - "A schedule, at 6:00 AM" tells the owner nobody is
    /// waiting on it. This is the fact behind that word, and it is a record rather than a reading of the row.
    /// </summary>
    [Fact]
    public void StartedSession_IsTrueForASessionAFireNamedAndFalseForOneItDidNot()
    {
        var store = new CronRunHistoryStore(_h.Open(), LegacyPath());
        store.Append("cj_1", Run("sid-a"));

        Assert.True(store.StartedSession(TenantId.Local, "sid-a"));
        Assert.False(store.StartedSession(TenantId.Local, "sid-b"));
    }

    /// <summary>Nothing is claimed about a session that is not asked about, and nothing throws on the empty
    /// arguments a caller can reach this with.</summary>
    [Fact]
    public void StartedSession_AnswersFalseForNothingToLookFor()
    {
        var store = new CronRunHistoryStore(_h.Open(), LegacyPath());
        store.Append("cj_1", Run("sid-a"));

        Assert.False(store.StartedSession(TenantId.Local, ""));
        Assert.False(store.StartedSession(TenantId.Local, "   "));
        Assert.False(store.StartedSession(default, "sid-a"));
    }

    [Fact]
    public void Append_BeyondCap_PrunesOldest()
    {
        var store = new CronRunHistoryStore(_h.Open(), LegacyPath());

        // Fill to exactly the cap.
        for (var i = 0; i < CronRunHistoryStore.MaxRecordsPerJob; i++)
            store.Append("cj_1", Run("sid-" + i));
        Assert.Equal(CronRunHistoryStore.MaxRecordsPerJob, store.List("cj_1").Count);

        // Every further append inserts AND prunes atomically, so the observable count stays AT the cap and
        // is never transiently one over it (the insert+prune is a single SaveChanges).
        for (var i = CronRunHistoryStore.MaxRecordsPerJob; i < CronRunHistoryStore.MaxRecordsPerJob + 10; i++)
        {
            store.Append("cj_1", Run("sid-" + i));
            Assert.Equal(CronRunHistoryStore.MaxRecordsPerJob, store.List("cj_1").Count);
        }

        var runs = store.List("cj_1");
        Assert.Equal(CronRunHistoryStore.MaxRecordsPerJob, runs.Count);
        // newest (highest i) retained at the top, oldest pruned.
        Assert.Equal("sid-" + (CronRunHistoryStore.MaxRecordsPerJob + 9), runs[0].SessionId);
        Assert.Equal("sid-10", runs[^1].SessionId);
    }

    [Fact]
    public void Append_KeepsRunsPerJobSeparate()
    {
        var store = new CronRunHistoryStore(_h.Open(), LegacyPath());
        store.Append("cj_1", Run("a1"));
        store.Append("cj_2", Run("b1"));
        store.Append("cj_1", Run("a2"));

        Assert.Equal(new[] { "a2", "a1" }, store.List("cj_1").Select(r => r.SessionId));
        Assert.Equal(new[] { "b1" }, store.List("cj_2").Select(r => r.SessionId));
    }

    [Fact]
    public void Persistence_RoundTrip_SurvivesReload_NewestFirstAndFields()
    {
        var legacy = LegacyPath();
        var store = new CronRunHistoryStore(_h.Open(), legacy);
        store.Append("cj_1", Run("sid-a"));
        store.Append("cj_1", Run("sid-b"));
        store.Append("cj_2", Run("sid-c"));

        var reloaded = new CronRunHistoryStore(_h.Open(), legacy);
        var cj1 = reloaded.List("cj_1");
        Assert.Equal(2, cj1.Count);
        Assert.Equal("sid-b", cj1[0].SessionId);   // newest-first order survived
        Assert.Equal("sid-a", cj1[1].SessionId);
        Assert.Single(reloaded.List("cj_2"));

        // Every field round-tripped, timestamps as UTC.
        Assert.Equal("workstation-A", cj1[0].Machine);
        Assert.Equal("director-1", cj1[0].TargetDirectorId);
        Assert.Equal("unknown", cj1[0].TaskStatus);
        Assert.Equal(DateTimeKind.Utc, cj1[0].FiredUtc.Kind);
        Assert.Equal(new DateTime(2026, 6, 17, 5, 0, 3, DateTimeKind.Utc), cj1[0].FiredUtc);
    }

    [Fact]
    public void List_NoRuns_ReturnsEmpty()
    {
        var store = new CronRunHistoryStore(_h.Open(), LegacyPath());
        Assert.Empty(store.List("cj_unknown"));
    }

    [Fact]
    public void LegacyJson_ImportedOnce_PreservesNewestFirstAndCap_ThenRenamedAside()
    {
        // Legacy cronruns.json: one job with MORE than the cap (newest-first), plus a second small job.
        var legacy = LegacyPath();
        var big = new List<CronRunRecord>();
        for (var i = CronRunHistoryStore.MaxRecordsPerJob + 9; i >= 0; i--)  // newest-first: index 0 is newest
            big.Add(Run("sid-" + i));
        var small = new List<CronRunRecord> { Run("only-b"), Run("only-a") };
        WriteLegacyRunsFile(legacy, new Dictionary<string, List<CronRunRecord>>
        {
            ["cj_big"] = big,
            ["cj_small"] = small,
        });

        var store = new CronRunHistoryStore(_h.Open(), legacy);

        var imported = store.List("cj_big");
        // The per-job cap held: only the newest 50 survived the import.
        Assert.Equal(CronRunHistoryStore.MaxRecordsPerJob, imported.Count);
        // Newest-first order held: the newest record (highest i) is first.
        Assert.Equal("sid-" + (CronRunHistoryStore.MaxRecordsPerJob + 9), imported[0].SessionId);
        Assert.Equal("sid-10", imported[^1].SessionId);

        var smallImported = store.List("cj_small");
        Assert.Equal(new[] { "only-b", "only-a" }, smallImported.Select(r => r.SessionId));
        Assert.Equal("workstation-A", smallImported[0].Machine);

        // Renamed aside as a backup; not re-imported by a fresh store.
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(legacy)!, Path.GetFileName(legacy) + ".migrated-*"));
        Assert.Equal(CronRunHistoryStore.MaxRecordsPerJob, new CronRunHistoryStore(_h.Open(), legacy).List("cj_big").Count);
    }

    [Fact]
    public void CorruptLegacyJson_FailsLoud_AndLeavesTheFileInPlace()
    {
        var legacy = LegacyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "{ not json !!!");

        Assert.Throws<InvalidOperationException>(() => new CronRunHistoryStore(_h.Open(), legacy));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void Constructor_EmptyLegacyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CronRunHistoryStore(_h.Open(), " "));
    }

    private static void WriteLegacyRunsFile(string path, Dictionary<string, List<CronRunRecord>> runs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(new { Runs = runs }, options));
    }
}
