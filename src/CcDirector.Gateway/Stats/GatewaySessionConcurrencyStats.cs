using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's durable record of fleet CONCURRENCY and an hourly activity log. Unlike the input tally, a
/// session count needs no per-Director instrumentation: the Gateway's assembled /sessions roster already
/// sees every session on every machine (new build or old), so this is fleet-wide from day one. Observed
/// from that same assembled roster on every read.
///
/// Two headline series track the peak "how many at once":
///   - LIVE: sessions loaded/running (non-exited) - parallel capacity in flight.
///   - WORKING: sessions whose agent is processing a turn right now (activityState = Working).
/// Each keeps the current value and the all-time peak (+when).
///
/// An hourly log (keyed by UTC clock hour "yyyy-MM-ddTHH") records, for every hour:
///   - the max concurrent LIVE and WORKING seen that hour (the "24-hour chart" series),
///   - how many DISTINCT sessions, machines, and repositories were seen that hour (the "how much ran"
///     totals). Distinct counts dedupe across the hour's observations via a current-hour id set that is
///     persisted too, so a Gateway restart mid-hour keeps counting the same hour correctly.
/// Weekly max (or any window) is DERIVED from the hourly log. Persisted (atomic temp-write + rename,
/// corrupt file quarantined); hourly buckets past the retention window are pruned so the store stays
/// bounded.
///
/// MTR-08 (production-readiness census rows 49-67): every peak, hour bucket and current-hour dedup set is
/// kept PER TENANT. The /sessions roster is assembled per request tenant, so the concurrency it folds
/// belongs to one tenant; keeping the peaks per tenant means one account's "how many at once" can never mix
/// with another's. On the single-tenant self host there is exactly one tenant (<see cref="TenantId.Local"/>)
/// and the on-disk shape is the same numbers it always held, now under one tenant key. The dashboard that
/// reads a snapshot is refused in whole on hosted (issue #1848), so a reader here only ever runs for Local.
/// </summary>
public sealed class GatewaySessionConcurrencyStats : ISessionConcurrencyRecorder
{
    /// <inheritdoc />
    public StatsFailureCounters Health { get; } = new("concurrency-json");

    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };
    private const int RetentionDays = 90;
    private const string HourFormat = "yyyy-MM-ddTHH";

    /// <summary>The on-disk envelope version. 1 = the pre-tenant single-object shape (migrated to the local
    /// tenant on load); 2 = the per-tenant shape written from here on.</summary>
    private const int StoreVersion = 2;

    private readonly string _path;
    private readonly object _lock = new();

    // Per tenant (MTR-08). Created on first observation of a tenant; the whole of the pre-tenant state now
    // lives inside one of these.
    private readonly Dictionary<TenantId, TenantState> _tenants = new();

    private sealed class TenantState
    {
        public int LiveCurrent;
        public int WorkingCurrent;
        public int LiveAllTimeMax;
        public DateTime? LiveAllTimeMaxAtUtc;
        public int WorkingAllTimeMax;
        public DateTime? WorkingAllTimeMaxAtUtc;

        // Per-hour log, keyed by UTC clock hour.
        public readonly Dictionary<string, HourStat> Hours = new(StringComparer.Ordinal);

        // Dedup sets for the CURRENT hour only, so distinct counts are correct across many observations.
        // Rolled when the clock hour changes; persisted so a restart mid-hour resumes the same hour's dedup.
        public string CurrentHourKey = "";
        public readonly HashSet<string> CurSessions = new(StringComparer.Ordinal);
        public readonly HashSet<string> CurMachines = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> CurRepos = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class HourStat
    {
        public int MaxLive;
        public int MaxWorking;
        public int DistinctSessions;
        public int DistinctMachines;
        public int DistinctRepos;
    }

    /// <param name="path">The durable store file. Defaults to gateway-concurrency-stats.json under the
    /// cc-director storage root, beside the other Gateway stores.</param>
    public GatewaySessionConcurrencyStats(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? PathUnder(CcStorage.Root())
            : path!;
        Load();
    }

    /// <summary>The store file under <paramref name="storageRoot"/>: what a host whose root that is uses.</summary>
    public static string PathUnder(string storageRoot) => Path.Combine(storageRoot, "gateway-concurrency-stats.json");

    private static string HourKey(DateTime utc) =>
        utc.ToUniversalTime().ToString(HourFormat, CultureInfo.InvariantCulture);

    // Resolve the tenant a producer or reader supplied. Null is the self-host / unit-test default (Local); an
    // explicitly-passed tenant must be valid - an invalid one is a DENY, never silently defaulted.
    private static TenantId RequireTenant(TenantId? tenant)
    {
        if (tenant is not { } t) return TenantId.Local;
        if (!t.IsValid) throw new ArgumentException("a valid tenant is required", nameof(tenant));
        return t;
    }

    private TenantState StateFor(TenantId tenant)
    {
        if (!_tenants.TryGetValue(tenant, out var st))
        {
            st = new TenantState();
            _tenants[tenant] = st;
        }
        return st;
    }

    /// <summary>
    /// Observe the current fleet <paramref name="roster"/> for <paramref name="tenant"/> at
    /// <paramref name="nowUtc"/>: update that tenant's live and working current values and all-time peaks, and
    /// fold this hour's max concurrency plus its distinct session / machine / repository counts. Idempotent
    /// within an hour - a peak or distinct count only ever grows - so folding on every /sessions read captures
    /// the hourly log without inflating anything. Defaults to <see cref="TenantId.Local"/> for the self-host
    /// shape and the unit-test default; the production /sessions path passes the request tenant.
    /// </summary>
    public void Observe(IReadOnlyCollection<SessionDto>? roster, DateTime nowUtc, TenantId? tenant = null)
    {
        if (roster is null) return;
        var t = RequireTenant(tenant);
        var key = HourKey(nowUtc);
        lock (_lock)
        {
            var st = StateFor(t);
            if (key != st.CurrentHourKey)
            {
                st.CurrentHourKey = key;
                st.CurSessions.Clear();
                st.CurMachines.Clear();
                st.CurRepos.Clear();
            }

            var liveCount = 0;
            var workingCount = 0;
            foreach (var s in roster)
            {
                if (string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase)) continue;
                liveCount++;
                if (string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)) workingCount++;
                if (!string.IsNullOrEmpty(s.SessionId)) st.CurSessions.Add(s.SessionId);
                if (!string.IsNullOrWhiteSpace(s.MachineName)) st.CurMachines.Add(s.MachineName);
                if (!string.IsNullOrWhiteSpace(s.RepoPath)) st.CurRepos.Add(s.RepoPath);
            }

            st.LiveCurrent = liveCount;
            st.WorkingCurrent = workingCount;

            var changed = false;
            if (liveCount > st.LiveAllTimeMax) { st.LiveAllTimeMax = liveCount; st.LiveAllTimeMaxAtUtc = nowUtc; changed = true; }
            if (workingCount > st.WorkingAllTimeMax) { st.WorkingAllTimeMax = workingCount; st.WorkingAllTimeMaxAtUtc = nowUtc; changed = true; }

            if (!st.Hours.TryGetValue(key, out var h))
            {
                h = new HourStat();
                st.Hours[key] = h;
                changed = true;
            }
            if (liveCount > h.MaxLive) { h.MaxLive = liveCount; changed = true; }
            if (workingCount > h.MaxWorking) { h.MaxWorking = workingCount; changed = true; }
            if (st.CurSessions.Count > h.DistinctSessions) { h.DistinctSessions = st.CurSessions.Count; changed = true; }
            if (st.CurMachines.Count > h.DistinctMachines) { h.DistinctMachines = st.CurMachines.Count; changed = true; }
            if (st.CurRepos.Count > h.DistinctRepos) { h.DistinctRepos = st.CurRepos.Count; changed = true; }

            if (changed)
            {
                PruneLocked(st, nowUtc);
                Save();
            }
        }
    }

    /// <summary>A read-only snapshot for the dashboard / agent API for <paramref name="tenant"/>: the live and
    /// working series (current, all-time peak, derived weekly max) and the full hourly log. An unseen tenant
    /// returns an all-zero snapshot with no hours (MTR-08).</summary>
    public ConcurrencySnapshot Snapshot(DateTime nowUtc, TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        lock (_lock)
        {
            if (!_tenants.TryGetValue(t, out var st))
                return new ConcurrencySnapshot(
                    new ConcurrencySeriesDto(), new ConcurrencySeriesDto(), Array.Empty<ConcurrencyHourDto>());

            var weeklyCutoff = nowUtc.AddDays(-7);
            var liveWeekly = 0;
            var workingWeekly = 0;
            var hourly = new List<ConcurrencyHourDto>(st.Hours.Count);
            foreach (var kvp in st.Hours)
            {
                var h = kvp.Value;
                hourly.Add(new ConcurrencyHourDto
                {
                    Hour = kvp.Key,
                    MaxLive = h.MaxLive,
                    MaxWorking = h.MaxWorking,
                    Sessions = h.DistinctSessions,
                    Machines = h.DistinctMachines,
                    Repos = h.DistinctRepos,
                });
                if (TryParseHour(kvp.Key, out var dt) && dt >= weeklyCutoff)
                {
                    if (h.MaxLive > liveWeekly) liveWeekly = h.MaxLive;
                    if (h.MaxWorking > workingWeekly) workingWeekly = h.MaxWorking;
                }
            }
            hourly.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
            return new ConcurrencySnapshot(
                new ConcurrencySeriesDto { Current = st.LiveCurrent, AllTimeMax = st.LiveAllTimeMax, AllTimeMaxAtUtc = st.LiveAllTimeMaxAtUtc, WeeklyMax = liveWeekly },
                new ConcurrencySeriesDto { Current = st.WorkingCurrent, AllTimeMax = st.WorkingAllTimeMax, AllTimeMaxAtUtc = st.WorkingAllTimeMaxAtUtc, WeeklyMax = workingWeekly },
                hourly);
        }
    }

    private void PruneLocked(TenantState st, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        var stale = st.Hours.Keys.Where(k => TryParseHour(k, out var dt) && dt < cutoff).ToList();
        foreach (var k in stale) st.Hours.Remove(k);
    }

    private static bool TryParseHour(string key, out DateTime utc) =>
        DateTime.TryParseExact(key, HourFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private sealed class HourStatStore
    {
        public int MaxLive { get; set; }
        public int MaxWorking { get; set; }
        public int DistinctSessions { get; set; }
        public int DistinctMachines { get; set; }
        public int DistinctRepos { get; set; }
    }

    // One tenant's persisted state. This is EXACTLY the pre-tenant StoreFile shape, so a version-1 file (which
    // held one of these at the root) is read straight into the local tenant's slot with no field remapping.
    private sealed class TenantStoreFile
    {
        public int LiveAllTimeMax { get; set; }
        public DateTime? LiveAllTimeMaxAtUtc { get; set; }
        public int WorkingAllTimeMax { get; set; }
        public DateTime? WorkingAllTimeMaxAtUtc { get; set; }
        public Dictionary<string, HourStatStore> Hours { get; set; } = new();
        public string CurrentHourKey { get; set; } = "";
        public List<string> CurrentSessions { get; set; } = new();
        public List<string> CurrentMachines { get; set; } = new();
        public List<string> CurrentRepos { get; set; } = new();
    }

    // The version-2 envelope: the version tag plus one TenantStoreFile per tenant.
    private sealed class StoreEnvelope
    {
        public int Version { get; set; }
        public Dictionary<string, TenantStoreFile> Tenants { get; set; } = new();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[GatewaySessionConcurrencyStats] Load: no store file at {_path}; starting empty");
            return;
        }

        string text;
        bool isVersioned;
        try
        {
            text = File.ReadAllText(_path);
            // The version-1 file is a bare TenantStoreFile (LiveAllTimeMax at the root); the version-2 file is
            // an envelope with a "Tenants" object. Detect by presence of the envelope's tenants property
            // (case-insensitively, so a serializer naming-policy change cannot silently misclassify the file)
            // so an upgrade migrates the old numbers into the local tenant rather than quarantining them.
            using var probe = JsonDocument.Parse(text);
            isVersioned = probe.RootElement.ValueKind == JsonValueKind.Object
                          && probe.RootElement.EnumerateObject()
                                  .Any(p => string.Equals(p.Name, "tenants", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        try
        {
            if (isVersioned)
            {
                var env = JsonSerializer.Deserialize<StoreEnvelope>(text, FileJsonOptions);
                if (env is null) { Quarantine("file deserialized to null (no store envelope)"); return; }
                foreach (var (tenantValue, tsf) in env.Tenants)
                    LoadTenant(new TenantId(tenantValue), tsf);
            }
            else
            {
                // Version 1: the whole file is the local tenant's state.
                var tsf = JsonSerializer.Deserialize<TenantStoreFile>(text, FileJsonOptions);
                if (tsf is null) { Quarantine("file deserialized to null (no store document)"); return; }
                LoadTenant(TenantId.Local, tsf);
            }
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        var totalHours = _tenants.Values.Sum(s => s.Hours.Count);
        FileLog.Write($"[GatewaySessionConcurrencyStats] Load: {_tenants.Count} tenant(s), {totalHours} hourly bucket(s) total from {_path}");
    }

    private void LoadTenant(TenantId tenant, TenantStoreFile parsed)
    {
        var st = StateFor(tenant);
        st.LiveAllTimeMax = parsed.LiveAllTimeMax;
        st.LiveAllTimeMaxAtUtc = parsed.LiveAllTimeMaxAtUtc;
        st.WorkingAllTimeMax = parsed.WorkingAllTimeMax;
        st.WorkingAllTimeMaxAtUtc = parsed.WorkingAllTimeMaxAtUtc;
        foreach (var (hour, hs) in parsed.Hours)
            st.Hours[hour] = new HourStat
            {
                MaxLive = hs.MaxLive,
                MaxWorking = hs.MaxWorking,
                DistinctSessions = hs.DistinctSessions,
                DistinctMachines = hs.DistinctMachines,
                DistinctRepos = hs.DistinctRepos,
            };
        st.CurrentHourKey = parsed.CurrentHourKey ?? "";
        foreach (var s in parsed.CurrentSessions) st.CurSessions.Add(s);
        foreach (var m in parsed.CurrentMachines) st.CurMachines.Add(m);
        foreach (var r in parsed.CurrentRepos) st.CurRepos.Add(r);
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[GatewaySessionConcurrencyStats] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    // Write-through under the lock: serialize the whole store and atomically replace the file (temp +
    // rename) so a concurrent reader or a crash mid-write never sees a half-written store. A failed save is
    // a LOGGED error that propagates - never a silent skip.
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var env = new StoreEnvelope { Version = StoreVersion };
            foreach (var (tenant, st) in _tenants)
            {
                var tsf = new TenantStoreFile
                {
                    LiveAllTimeMax = st.LiveAllTimeMax,
                    LiveAllTimeMaxAtUtc = st.LiveAllTimeMaxAtUtc,
                    WorkingAllTimeMax = st.WorkingAllTimeMax,
                    WorkingAllTimeMaxAtUtc = st.WorkingAllTimeMaxAtUtc,
                    CurrentHourKey = st.CurrentHourKey,
                    CurrentSessions = st.CurSessions.ToList(),
                    CurrentMachines = st.CurMachines.ToList(),
                    CurrentRepos = st.CurRepos.ToList(),
                };
                foreach (var (hour, h) in st.Hours)
                    tsf.Hours[hour] = new HourStatStore
                    {
                        MaxLive = h.MaxLive,
                        MaxWorking = h.MaxWorking,
                        DistinctSessions = h.DistinctSessions,
                        DistinctMachines = h.DistinctMachines,
                        DistinctRepos = h.DistinctRepos,
                    };
                env.Tenants[tenant.Value] = tsf;
            }

            var json = JsonSerializer.Serialize(env, FileJsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewaySessionConcurrencyStats] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}

/// <summary>One tracked concurrency dimension (live or working): current, all-time peak (+when), and the
/// derived weekly max.</summary>
public sealed class ConcurrencySeriesDto
{
    public int Current { get; set; }
    public int AllTimeMax { get; set; }
    public DateTime? AllTimeMaxAtUtc { get; set; }
    public int WeeklyMax { get; set; }
}

/// <summary>One hour of the fleet activity log (UTC clock hour "yyyy-MM-ddTHH"): the max concurrent live
/// and working counts, and how many distinct sessions, machines, and repositories were seen that hour.</summary>
public sealed class ConcurrencyHourDto
{
    public string Hour { get; set; } = "";
    public int MaxLive { get; set; }
    public int MaxWorking { get; set; }
    public int Sessions { get; set; }
    public int Machines { get; set; }
    public int Repos { get; set; }
}

/// <summary>The concurrency snapshot: both headline series plus the hourly activity log (oldest hour first).</summary>
public sealed record ConcurrencySnapshot(
    ConcurrencySeriesDto Live,
    ConcurrencySeriesDto Working,
    IReadOnlyList<ConcurrencyHourDto> Hourly);
