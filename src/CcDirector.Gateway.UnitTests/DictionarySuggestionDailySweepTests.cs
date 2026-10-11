using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The daily suggestion sweep (devthrottle #2115). The due rule is pure and pinned first: a tenant is due
/// when it never scanned, or when the next tenant-local 00:05 after its last scan is in the past - so the
/// SAME timer tick scans Sydney at Sydney's midnight and Copenhagen at Copenhagen's, and running the sweep
/// twice in one day does not scan twice. Then one self-host integration proof: the sweep actually runs the
/// scan through the seam and stores the result, and a second sweep the same day is a no-op.
/// </summary>
public sealed class DictionarySuggestionDailySweepTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static DateTime Utc_(int y, int mo, int d, int h, int mi)
        => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void IsDue_NeverScanned_IsDueImmediately()
        => Assert.True(DictionarySuggestionDailySweep.IsDue(null, Utc, Utc_(2026, 7, 24, 12, 0)));

    [Fact]
    public void IsDue_ScannedEarlierToday_NotDueUntilTomorrowsLocalMidnight()
    {
        var lastScan = Utc_(2026, 7, 24, 0, 10);
        Assert.False(DictionarySuggestionDailySweep.IsDue(lastScan, Utc, Utc_(2026, 7, 24, 23, 59)));
        Assert.True(DictionarySuggestionDailySweep.IsDue(lastScan, Utc, Utc_(2026, 7, 25, 0, 5)));
    }

    [Fact]
    public void IsDue_JustBeforeLocal0005_NotDue_JustAfter_Due()
    {
        var lastScan = Utc_(2026, 7, 23, 12, 0);
        Assert.False(DictionarySuggestionDailySweep.IsDue(lastScan, Utc, Utc_(2026, 7, 24, 0, 4)));
        Assert.True(DictionarySuggestionDailySweep.IsDue(lastScan, Utc, Utc_(2026, 7, 24, 0, 5)));
    }

    [Fact]
    public void IsDue_HonorsTheTenantsOwnZone()
    {
        // Last scan 2026-07-23 13:00 UTC = 23:00 that day in Sydney (winter, UTC+10). At 14:10 UTC it is
        // already 00:10 of JULY 24 in Sydney - past Sydney's 00:05, so the Sydney tenant is due - while a
        // UTC tenant's midnight is still ten hours away.
        var lastScan = Utc_(2026, 7, 23, 13, 0);
        var now = Utc_(2026, 7, 23, 14, 10);
        Assert.True(DictionarySuggestionDailySweep.IsDue(lastScan, Sydney, now));
        Assert.False(DictionarySuggestionDailySweep.IsDue(lastScan, Utc, now));
    }

    // ---- integration: the sweep runs the scan through the seam and stores the result ----

    private sealed class CountingBrain : IAgentBrain
    {
        public int Calls;
        public string? SessionId => null;
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Calls++;
            // Approve every candidate line in the prompt (lines like: 1. "term" heard as: ...).
            var verdicts = new List<object>();
            foreach (var line in prompt.Split('\n'))
            {
                var t = line.Trim();
                var dot = t.IndexOf(". \"", StringComparison.Ordinal);
                if (dot < 0 || dot > 4) continue;
                var start = dot + 3;
                var end = t.IndexOf('"', start);
                if (end < 0) continue;
                verdicts.Add(new { term = t[start..end], approved = true, reason = "stub" });
            }
            return Task.FromResult(new AskResult { Text = JsonSerializer.Serialize(verdicts) });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    [Fact]
    public async Task Sweep_SelfHost_SeedsTheFirstScan_AndDoesNotRescanTheSameDay()
    {
        using var h = new GatewayDbTestHarness();
        var ctx = new SingleTenantContext();
        var registry = new TenantRegistry(h.Open(ctx));
        var boundary = new HostedTenantBoundary(ctx, new DeviceRegistry(), hosted: false);

        var transcripts = new TranscriptStore(h.Open(ctx));
        var scans = new DictionarySuggestionScanStore(h.Open(ctx));
        var brain = new CountingBrain();
        var clock = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var emptyDict = new DictationDictionary(
            Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile> { ["default"] = new("default", true) });

        // The full chained corpus (Mindzee bridges mindzie ~ Mindsee below the direct-link threshold),
        // matching the canonical corpus the miner and service tests pin.
        var i = 0;
        foreach (var (spelling, times) in new[] { ("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12) })
            for (var n = 0; n < times; n++)
                transcripts.Append(TenantId.Local, "dictation", $"the {spelling} change", null, false,
                    turnId: null, nowUtc: clock.AddSeconds(i++));

        var service = new DictionarySuggestionService(
            transcripts,
            new DictionarySuggestionDismissalStore(h.Open(ctx)),
            new DictionarySuggestionVerdictStore(h.Open(ctx)),
            scans,
            _ => emptyDict,
            (_, _) => Task.FromResult<(IAgentBrain, string)>((brain, "stub-model")),
            now: () => clock);
        var sweep = new DictionarySuggestionDailySweep(
            boundary, registry, ctx, service,
            new TenantSettingsResolver(new TenantSettingsStore(h.Open(ctx))),
            now: () => clock);

        // No stored scan: the first sweep is due immediately and seeds the stored result.
        await sweep.SweepAsync();
        Assert.Equal(1, brain.Calls);
        var stored = scans.Get(TenantId.Local);
        Assert.NotNull(stored);
        Assert.Equal("mindzie", Assert.Single(stored!.Suggestions).Term);

        // Same day, later tick: not due, nothing runs.
        clock = clock.AddHours(3);
        await sweep.SweepAsync();
        Assert.Equal(1, brain.Calls);
        Assert.Equal(stored.ScannedAtUtc, scans.Get(TenantId.Local)!.ScannedAtUtc);
    }
}
