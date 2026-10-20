using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Licenses;

/// <summary>One session as the recorder saw it in a connected Director's roster.</summary>
/// <param name="AccountEmail">The Claude account its machine is on, lower-cased; null when not known, and always null for
/// an agent that does not run on a Claude account.</param>
/// <param name="Totals">The running token totals it reports; null when it reports none (yet).</param>
public sealed record SessionUsageObservation(
    string SessionId,
    string DirectorId,
    string AgentKind,
    int? Number,
    string? Name,
    Guid? MissionId,
    string? MissionName,
    string? ControllerSessionId,
    string MachineName,
    string? Model,
    string State,
    DateTime StartedAtUtc,
    string? AccountEmail,
    string? AgentConversationId,
    TokenTotalsDto? Totals);

/// <summary>One account's usage reading, as the recorder keeps it.</summary>
public sealed record LicenseUsageReadingRecord(
    string AccountEmail,
    DateTime AtUtc,
    double? SevenDayUtilization,
    DateTime? SevenDayResetsAtUtc,
    double? FiveHourUtilization,
    DateTime? FiveHourResetsAtUtc);

/// <summary>The weighted tokens one session used on one account (the empty string when not known) in some span.</summary>
public sealed record SessionAccountUse(string SessionId, string AccountEmail, double Weighted);

/// <summary>
/// THE GATEWAY'S RECORD OF WHAT IS USING THE LICENSE (the usage-by-session mandate of 2026-10-20), over
/// <c>session_usage</c>, <c>session_usage_samples</c> and <c>license_usage_readings</c>. Written once a minute by
/// <see cref="SessionUsageRecorder"/>; read by <see cref="SessionUsageFold"/>.
///
/// A SESSION KEEPS ONE RUNNING TOTAL. Each report's change (<see cref="SessionUsageMath.Change"/>) is added to the
/// session's weighted total and written as one sample, tagged with the account the session's machine was on. The FIRST
/// time a session is seen it may already have a long life behind it: that use goes into its total (so "so far" is
/// whole-life) but writes NO sample, because a sample says WHEN tokens were used and that use was not now.
///
/// Kept for a bounded time: samples and readings for <see cref="SampleRetention"/> and <see cref="ReadingRetention"/>
/// (a week and a day - the longest span anything reads them over), and a session row for <see cref="SessionRetention"/>
/// after it was last seen. Threading matches the rest of the data layer: a write lock, a fresh context per operation.
/// </summary>
public sealed class SessionUsageStore
{
    public static readonly TimeSpan SampleRetention = TimeSpan.FromDays(8);
    public static readonly TimeSpan ReadingRetention = TimeSpan.FromDays(8);
    public static readonly TimeSpan SessionRetention = TimeSpan.FromDays(60);

    /// <summary>A reading is kept when its numbers changed, or when this long has passed since the last one kept.</summary>
    public static readonly TimeSpan ReadingKeptAtLeastEvery = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public SessionUsageStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Take one minute's observations of <paramref name="tenant"/>'s live sessions. Returns how many samples were
    /// written. One save: the whole minute lands, or none of it.
    /// </summary>
    public int Observe(TenantId tenant, IReadOnlyList<SessionUsageObservation> seen, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(seen);
        if (seen.Count == 0)
            return 0;
        var now = Utc(nowUtc);
        var samples = 0;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var ids = seen.Select(s => s.SessionId).Distinct(StringComparer.Ordinal).ToList();
            var rows = ctx.SessionUsage.Where(r => ids.Contains(r.SessionId)).ToDictionary(r => r.SessionId, StringComparer.Ordinal);
            foreach (var s in seen)
            {
                if (!rows.TryGetValue(s.SessionId, out var row))
                {
                    row = new SessionUsageEntity { SessionId = s.SessionId, FirstSeenAtUtc = now, TenantId = ctx.ActiveTenant! };
                    ctx.SessionUsage.Add(row);
                    rows[s.SessionId] = row;
                }
                Describe(row, s, now);
                if (s.Totals is not { } totals)
                    continue;

                var next = RawTokens.Of(totals);
                var sameConversation = string.Equals(row.AgentConversationId, s.AgentConversationId, StringComparison.Ordinal);
                RawTokens? previous = row.HasBaseline
                    ? new RawTokens(row.LastInputTokens, row.LastOutputTokens, row.LastCacheReadTokens, row.LastCacheCreationTokens)
                    : null;
                var change = SessionUsageMath.Change(previous, next, sameConversation);
                var weighted = SessionUsageMath.Weighted(change);
                if (weighted > 0)
                {
                    row.WeightedTotal += weighted;
                    row.WeightedByAccountJson = AddToAccount(row.WeightedByAccountJson, s.AccountEmail, weighted);
                    if (row.HasBaseline)
                    {
                        ctx.SessionUsageSamples.Add(new SessionUsageSampleEntity
                        {
                            SessionId = s.SessionId,
                            AtUtc = now,
                            Weighted = weighted,
                            InputTokens = change.Input,
                            OutputTokens = change.Output,
                            CacheReadTokens = change.CacheRead,
                            CacheCreationTokens = change.CacheCreation,
                            Model = Cap(s.Model, 128),
                            AccountEmail = s.AccountEmail,
                            TenantId = ctx.ActiveTenant!,
                        });
                        samples++;
                    }
                }
                row.HasBaseline = true;
                row.AgentConversationId = Cap(s.AgentConversationId, 128);
                row.LastInputTokens = next.Input;
                row.LastOutputTokens = next.Output;
                row.LastCacheReadTokens = next.CacheRead;
                row.LastCacheCreationTokens = next.CacheCreation;
            }
            ctx.SaveChanges();
        }
        FileLog.Write($"[SessionUsageStore] Observe: tenant={tenant.ToLogString()}, sessions={seen.Count}, samples={samples}");
        return samples;
    }

    /// <summary>
    /// Keep each account's reading when it says something new: its numbers changed since the last one kept, or
    /// <see cref="ReadingKeptAtLeastEvery"/> has passed. A reading at a time already kept is the same reading again.
    /// Returns how many were kept.
    /// </summary>
    public int RecordReadings(TenantId tenant, IReadOnlyList<LicenseUsageReadingRecord> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count == 0)
            return 0;
        var kept = 0;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            foreach (var r in readings)
            {
                var email = LicenseStore.NormalizeEmail(r.AccountEmail);
                var at = Utc(r.AtUtc);
                var last = ctx.LicenseUsageReadings.AsNoTracking()
                    .Where(x => x.AccountEmail == email)
                    .OrderByDescending(x => x.AtUtc)
                    .FirstOrDefault();
                if (last is not null)
                {
                    if (at <= Utc(last.AtUtc))
                        continue;
                    var same = last.SevenDayUtilization == r.SevenDayUtilization
                        && last.SevenDayResetsAtUtc == NullableUtc(r.SevenDayResetsAtUtc)
                        && last.FiveHourUtilization == r.FiveHourUtilization;
                    if (same && at - Utc(last.AtUtc) < ReadingKeptAtLeastEvery)
                        continue;
                }
                ctx.LicenseUsageReadings.Add(new LicenseUsageReadingEntity
                {
                    AccountEmail = email,
                    AtUtc = at,
                    SevenDayUtilization = r.SevenDayUtilization,
                    SevenDayResetsAtUtc = NullableUtc(r.SevenDayResetsAtUtc),
                    FiveHourUtilization = r.FiveHourUtilization,
                    FiveHourResetsAtUtc = NullableUtc(r.FiveHourResetsAtUtc),
                    TenantId = ctx.ActiveTenant!,
                });
                kept++;
            }
            if (kept > 0)
                ctx.SaveChanges();
        }
        if (kept > 0)
            FileLog.Write($"[SessionUsageStore] RecordReadings: tenant={tenant.ToLogString()}, kept={kept}");
        return kept;
    }

    /// <summary>Delete what is older than the retention spans. Returns the rows deleted.</summary>
    public int Prune(TenantId tenant, DateTime nowUtc)
    {
        var now = Utc(nowUtc);
        int deleted;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var samplesBefore = now - SampleRetention;
            var readingsBefore = now - ReadingRetention;
            var sessionsBefore = now - SessionRetention;
            deleted = ctx.SessionUsageSamples.Where(s => s.AtUtc < samplesBefore).ExecuteDelete()
                + ctx.LicenseUsageReadings.Where(r => r.AtUtc < readingsBefore).ExecuteDelete()
                + ctx.SessionUsage.Where(r => r.LastSeenAtUtc < sessionsBefore).ExecuteDelete();
        }
        FileLog.Write($"[SessionUsageStore] Prune: tenant={tenant.ToLogString()}, deleted={deleted}");
        return deleted;
    }

    /// <summary>Every session row last seen at or after <paramref name="sinceUtc"/>.</summary>
    public IReadOnlyList<SessionUsageEntity> ReadSessions(TenantId tenant, DateTime sinceUtc)
    {
        var since = Utc(sinceUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.SessionUsage.AsNoTracking().Where(r => r.LastSeenAtUtc >= since).ToList();
        }
    }

    /// <summary>The weighted tokens each session used on each account after <paramref name="fromUtc"/> (exclusive),
    /// summed in the database.</summary>
    public IReadOnlyList<SessionAccountUse> UseSince(TenantId tenant, DateTime fromUtc)
    {
        var from = Utc(fromUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.SessionUsageSamples.AsNoTracking()
                .Where(s => s.AtUtc > from)
                .GroupBy(s => new { s.SessionId, s.AccountEmail })
                .Select(g => new { g.Key.SessionId, g.Key.AccountEmail, Weighted = g.Sum(s => s.Weighted) })
                .ToList()
                .Select(g => new SessionAccountUse(g.SessionId, g.AccountEmail ?? "", g.Weighted))
                .ToList();
        }
    }

    /// <summary>The weighted tokens every session used on <paramref name="accountEmail"/> after
    /// <paramref name="fromUtc"/> and up to and including <paramref name="toUtc"/>.</summary>
    public double AccountUseBetween(TenantId tenant, string accountEmail, DateTime fromUtc, DateTime toUtc)
    {
        var email = LicenseStore.NormalizeEmail(accountEmail);
        var (from, to) = (Utc(fromUtc), Utc(toUtc));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.SessionUsageSamples.AsNoTracking()
                .Where(s => s.AccountEmail == email && s.AtUtc > from && s.AtUtc <= to)
                .Sum(s => (double?)s.Weighted) ?? 0;
        }
    }

    /// <summary>Every kept reading at or after <paramref name="sinceUtc"/>, oldest first.</summary>
    public IReadOnlyList<LicenseUsageReadingRecord> ReadReadings(TenantId tenant, DateTime sinceUtc)
    {
        var since = Utc(sinceUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.LicenseUsageReadings.AsNoTracking()
                .Where(r => r.AtUtc >= since)
                .OrderBy(r => r.AtUtc)
                .ToList()
                .Select(r => new LicenseUsageReadingRecord(r.AccountEmail, Utc(r.AtUtc), r.SevenDayUtilization,
                    NullableUtc(r.SevenDayResetsAtUtc), r.FiveHourUtilization, NullableUtc(r.FiveHourResetsAtUtc)))
                .ToList();
        }
    }

    /// <summary>The weighted tokens of a session row per account, read back from its JSON.</summary>
    public static IReadOnlyDictionary<string, double> WeightedByAccount(SessionUsageEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return JsonSerializer.Deserialize<Dictionary<string, double>>(row.WeightedByAccountJson)
               ?? throw new InvalidOperationException($"session_usage row {row.SessionId} has no per-account totals");
    }

    private static void Describe(SessionUsageEntity row, SessionUsageObservation s, DateTime now)
    {
        row.DirectorId = Cap(s.DirectorId, 128) ?? "";
        row.AgentKind = Cap(s.AgentKind, 32) ?? "";
        row.Number = s.Number;
        row.Name = Cap(s.Name, 200);
        row.MissionId = s.MissionId;
        row.MissionName = Cap(s.MissionName, 200);
        row.ControllerSessionId = Cap(s.ControllerSessionId, 64);
        row.MachineName = Cap(s.MachineName, 128) ?? "";
        row.Model = Cap(s.Model, 128) ?? row.Model;
        row.State = Cap(s.State, 32) ?? "";
        row.AccountEmail = s.AccountEmail;
        row.StartedAtUtc = Utc(s.StartedAtUtc);
        row.LastSeenAtUtc = now;
    }

    private static string AddToAccount(string json, string? accountEmail, double weighted)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new Dictionary<string, double>();
        var key = accountEmail ?? "";
        map[key] = (map.TryGetValue(key, out var had) ? had : 0) + weighted;
        return JsonSerializer.Serialize(map);
    }

    private static string? Cap(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static DateTime? NullableUtc(DateTime? value) => value is { } v ? Utc(v) : null;
}
