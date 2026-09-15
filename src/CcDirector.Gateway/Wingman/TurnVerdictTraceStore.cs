using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Wingman;

/// <summary>The words a trace's outcome is recorded under. Closed: a reader can list every one.</summary>
public static class TurnVerdictTraceOutcomes
{
    /// <summary>The judge answered and the contract accepted the answer.</summary>
    public const string Judged = "judged";
    /// <summary>The judge answered and the contract refused the answer.</summary>
    public const string Refused = "refused";
    /// <summary>No answer inside the timeout, or the call never arrived.</summary>
    public const string DidNotAnswer = "did-not-answer";
    /// <summary>The provider said "not now".</summary>
    public const string RateLimited = "rate-limited";
    /// <summary>The judge could not be asked at all, or the judgement met an exception nobody expected.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>A new stop on an unchanged screen: the stored verdict was used again and nobody was asked.</summary>
    public const string Reused = "reused";
    /// <summary>The carrying-on clock ran out and a needed-you verdict replaced the carrying-on one.</summary>
    public const string Expired = "expired";
}

/// <summary>
/// One judgement as the inspector reads it - see <see cref="TurnVerdictTraceEntity"/> for what each field is
/// and why the record exists apart from the verdicts.
/// </summary>
public sealed record TurnVerdictTrace
{
    public string TraceId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string DirectorId { get; init; } = "";
    public DateTime RecordedAtUtc { get; init; }
    public DateTime TurnEndObservedAtUtc { get; init; }
    public string Trigger { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string VerdictId { get; init; } = "";
    public string? ReplacedVerdictId { get; init; }
    public double? ReplySeconds { get; init; }
    public bool ColourEnabled { get; init; }
    public TurnVerdictPackage? Package { get; init; }
    public string? Prompt { get; init; }
    public string? RawReply { get; init; }
    public bool RawReplyTruncated { get; init; }
    public TurnVerdictDto Verdict { get; init; } = new();
}

/// <summary>
/// The Wingman inspector's record, over the <c>turn_verdict_traces</c> table: every judgement, appended and
/// never rewritten, kept seven days.
///
/// TENANT-PARTITIONED BY CONSTRUCTION, for the same reason as <see cref="TurnVerdictStore"/>: every method
/// names its tenant and opens its context through <see cref="GatewayDatabase.CreateContext(TenantId)"/>, and
/// there is no read by bare session id.
///
/// NOTHING HERE IS DELETED WHEN A SESSION WORKS. That is the whole reason this store exists apart from the
/// verdicts - see <see cref="TurnVerdictTraceEntity"/>. The retention purge is the only thing that removes a
/// row.
/// </summary>
public sealed class TurnVerdictTraceStore
{
    /// <summary>How long a trace is kept: the same seven days as the verdicts it records.</summary>
    public static TimeSpan RetentionPeriod => TurnVerdictStore.RetentionPeriod;

    /// <summary>The most traces one history read returns, whatever the caller asks for.</summary>
    public const int MaxHistoryCount = 200;

    /// <summary>The default history page when the caller names no count.</summary>
    public const int DefaultHistoryCount = 50;

    /// <summary>
    /// The ceiling on a stored raw reply, in characters. A well-behaved answer is a few thousand characters;
    /// this bounds the one that is not, because a judge's answer is not text this Gateway controls. A reply
    /// over it is cut and the cut is recorded, never hidden.
    /// </summary>
    public const int MaxRawReplyChars = 64_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public TurnVerdictTraceStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Append one trace. Insert only: a trace id that already exists is a defect, and throws.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid, or the trace carries no id, no session,
    /// no recorded moment, no observed moment, or no verdict id.</exception>
    public void Append(TenantId tenant, TurnVerdictTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Require(trace.TraceId, "A trace carries its own id.");
        Require(trace.SessionId, "A trace names the session it is about.");
        Require(trace.VerdictId, "A trace names the verdict record it stored.");
        if (trace.RecordedAtUtc == default)
            throw new ArgumentException("A trace carries the moment it was recorded.", nameof(trace));
        if (trace.TurnEndObservedAtUtc == default)
            throw new ArgumentException("A trace carries the moment the detector observed the stop.", nameof(trace));

        var raw = trace.RawReply;
        var truncated = raw is not null && raw.Length > MaxRawReplyChars;
        if (truncated) raw = raw![..MaxRawReplyChars];

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            ctx.TurnVerdictTraces.Add(new TurnVerdictTraceEntity
            {
                TenantId = ctx.ActiveTenant!,
                TraceId = trace.TraceId,
                SessionId = trace.SessionId,
                DirectorId = trace.DirectorId ?? "",
                RecordedAtUtc = Utc(trace.RecordedAtUtc),
                TurnEndObservedAtUtc = Utc(trace.TurnEndObservedAtUtc),
                Trigger = trace.Trigger,
                Outcome = trace.Outcome,
                VerdictId = trace.VerdictId,
                ReplacedVerdictId = trace.ReplacedVerdictId,
                ReplySeconds = trace.ReplySeconds,
                ColourEnabled = trace.ColourEnabled,
                PackageJson = trace.Package is null ? null : JsonSerializer.Serialize(trace.Package, JsonOptions),
                Prompt = trace.Prompt,
                RawReply = raw,
                RawReplyTruncated = truncated || trace.RawReplyTruncated,
                VerdictJson = JsonSerializer.Serialize(trace.Verdict, JsonOptions),
            });
            ctx.SaveChanges();
        }
    }

    /// <summary>This session's traces in this tenant, newest first, capped at <see cref="MaxHistoryCount"/>.</summary>
    public IReadOnlyList<TurnVerdictTrace> History(TenantId tenant, string sessionId, int count = DefaultHistoryCount)
    {
        Require(sessionId, "A session id is required.");
        var take = count <= 0 ? DefaultHistoryCount : Math.Min(count, MaxHistoryCount);
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.TurnVerdictTraces.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .OrderByDescending(t => t.RecordedAtUtc)
            .Take(take)
            .ToList();
        var list = new List<TurnVerdictTrace>(rows.Count);
        foreach (var row in rows)
        {
            var trace = Read(row);
            if (trace is not null) list.Add(trace);
        }
        return list;
    }

    /// <summary>Remove this tenant's traces older than <paramref name="cutoffUtc"/>. Called once per tenant by
    /// <see cref="TurnVerdictRetentionSweep"/>. Returns how many rows went.</summary>
    public int PurgeOlderThan(TenantId tenant, DateTime cutoffUtc)
    {
        var cutoff = Utc(cutoffUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var stale = ctx.TurnVerdictTraces.Where(t => t.RecordedAtUtc < cutoff).ToList();
            if (stale.Count == 0) return 0;
            ctx.TurnVerdictTraces.RemoveRange(stale);
            ctx.SaveChanges();
            return stale.Count;
        }
    }

    /// <summary>
    /// Read one stored trace back. A row whose JSON cannot be read answers null and is LOGGED with its full key,
    /// the rule <see cref="TurnVerdictStore"/> set for its own rows: a row written by a newer Gateway must not take
    /// the whole history down, and the log line is what makes the one bad row findable.
    /// </summary>
    private static TurnVerdictTrace? Read(TurnVerdictTraceEntity row)
    {
        try
        {
            return new TurnVerdictTrace
            {
                TraceId = row.TraceId,
                SessionId = row.SessionId,
                DirectorId = row.DirectorId,
                RecordedAtUtc = row.RecordedAtUtc,
                TurnEndObservedAtUtc = row.TurnEndObservedAtUtc,
                Trigger = row.Trigger,
                Outcome = row.Outcome,
                VerdictId = row.VerdictId,
                ReplacedVerdictId = row.ReplacedVerdictId,
                ReplySeconds = row.ReplySeconds,
                ColourEnabled = row.ColourEnabled,
                Package = row.PackageJson is null ? null : JsonSerializer.Deserialize<TurnVerdictPackage>(row.PackageJson, JsonOptions),
                Prompt = row.Prompt,
                RawReply = row.RawReply,
                RawReplyTruncated = row.RawReplyTruncated,
                Verdict = JsonSerializer.Deserialize<TurnVerdictDto>(row.VerdictJson, JsonOptions) ?? new TurnVerdictDto(),
            };
        }
        catch (JsonException ex)
        {
            FileLog.Write(
                $"[TurnVerdictTraceStore] a stored trace could not be read: tenant={row.TenantId} trace={row.TraceId} "
                + $"sid={row.SessionId}: {ex.Message}");
            return null;
        }
    }

    private static void Require(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(message);
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
