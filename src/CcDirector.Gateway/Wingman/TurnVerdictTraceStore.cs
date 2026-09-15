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
    /// <summary>A stop that stood down before anything was read or asked. Its cause says why. No verdict.</summary>
    public const string Skipped = "skipped";
    /// <summary>The session worked while the verdict was being formed, so nothing was stored. No verdict.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>A stop observed while a judgement for the same session was already running. It asked nothing of its
    /// own - one model call per stop - so it carries its own moment and no verdict. The running judgement's trace is
    /// the one with content, and it can carry this stop's moment too when its verdict adopted it.</summary>
    public const string Joined = "joined";

    /// <summary>True for the outcomes whose row carries no verdict record of its own.</summary>
    public static bool StoresNoVerdict(string outcome) => outcome is Skipped or Cancelled or Joined;
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
    public string? Cause { get; init; }
    public string? VerdictId { get; init; }
    public string? ReplacedVerdictId { get; init; }
    public double? ReplySeconds { get; init; }
    public bool ColourEnabled { get; init; }
    public TurnVerdictPackage? Package { get; init; }
    public bool PackageOmitted { get; init; }
    public string? Prompt { get; init; }
    public bool PromptTruncated { get; init; }
    public string? RawReply { get; init; }
    public bool RawReplyTruncated { get; init; }
    public TurnVerdictDto? Verdict { get; init; }
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
///
/// EVERY BULKY FIELD HAS A CEILING, so no one row is unbounded whatever a terminal printed or a judge answered:
/// the raw reply and the prompt are cut, the package is omitted whole when it is over (cutting JSON would leave
/// nothing readable), and a refusal reason is cut in this copy. Each cut is recorded, never hidden.
///
/// THERE IS NO CEILING ON HOW MANY ROWS AN ACCOUNT HAS, and that is a ruling rather than an oversight. The table
/// holds one row per stop and one per judge call, so it grows with the fleet's own activity, and seven days bounds
/// how long any of it stays. A per-account cap would have to choose which stops of a busy day to throw away - the
/// busiest day being the one a person most wants to inspect - and the verdict table it sits beside has no cap
/// either. What bounds a row is its ceilings; what bounds the table is the account's fleet and the week.
/// </summary>
public sealed class TurnVerdictTraceStore
{
    /// <summary>How long a trace is kept: the same seven days as the verdicts it records.</summary>
    public static TimeSpan RetentionPeriod => TurnVerdictStore.RetentionPeriod;

    /// <summary>The most traces one history read returns, whatever the caller asks for.</summary>
    public const int MaxHistoryCount = 200;

    /// <summary>The default history page when the caller names no count.</summary>
    public const int DefaultHistoryCount = 50;

    /// <summary>The ceiling on a stored raw reply, in characters. A well-behaved answer is a few thousand; this
    /// bounds the one that is not, because a judge's answer is not text this Gateway controls.</summary>
    public const int MaxRawReplyChars = 64_000;

    /// <summary>The ceiling on a stored prompt, in characters. The template is about sixteen thousand and the
    /// package's own fields are capped, so a normal prompt is well under half of this.</summary>
    public const int MaxPromptChars = 128_000;

    /// <summary>The ceiling on the serialized package, in characters. Over it, the package is omitted whole.</summary>
    public const int MaxPackageJsonChars = 256_000;

    /// <summary>The ceiling on a refusal reason in this copy of the verdict. The contract can quote the judge's
    /// own unknown word into its reason, so the reason is as long as the judge chose to make it.</summary>
    public const int MaxFailureReasonChars = 2_000;

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
    /// <exception cref="ArgumentException">The trace carries no id, no session, no outcome, no recorded moment or no
    /// observed moment; or an outcome that stores a verdict carries no verdict.</exception>
    /// <remarks>The ceilings are applied here too, so a caller that did not go through the writer cannot store more
    /// than a row may hold.</remarks>
    public void Append(TenantId tenant, TurnVerdictTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Require(trace.TraceId, "A trace carries its own id.");
        Require(trace.SessionId, "A trace names the session it is about.");
        Require(trace.Outcome, "A trace says how the judgement ended.");
        if (!TurnVerdictTraceOutcomes.StoresNoVerdict(trace.Outcome))
        {
            Require(trace.VerdictId, "A trace of a stored verdict names that verdict.");
            if (trace.Verdict is null)
                throw new ArgumentException("A trace of a stored verdict carries that verdict.", nameof(trace));
        }
        if (trace.RecordedAtUtc == default)
            throw new ArgumentException("A trace carries the moment it was recorded.", nameof(trace));
        if (trace.TurnEndObservedAtUtc == default)
            throw new ArgumentException("A trace carries the moment the detector observed the stop.", nameof(trace));

        trace = ApplyCeilings(trace);
        // The serialized length is checked HERE, on the writer's thread, where the cost and any fault belong; the check
        // before queueing is a cheap count of the text the package carries.
        var packageJson = trace.Package is null ? null : JsonSerializer.Serialize(trace.Package, JsonOptions);
        var packageOmitted = trace.PackageOmitted;
        if (packageJson is { Length: > MaxPackageJsonChars })
        {
            packageJson = null;
            packageOmitted = true;
        }

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
                Cause = trace.Cause,
                VerdictId = trace.VerdictId,
                ReplacedVerdictId = trace.ReplacedVerdictId,
                ReplySeconds = trace.ReplySeconds,
                ColourEnabled = trace.ColourEnabled,
                PackageJson = packageJson,
                PackageOmitted = packageOmitted,
                Prompt = trace.Prompt,
                PromptTruncated = trace.PromptTruncated,
                RawReply = trace.RawReply,
                RawReplyTruncated = trace.RawReplyTruncated,
                VerdictJson = trace.Verdict is null ? null : SerializeVerdict(trace.Verdict),
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
    /// The trace cut to what one row may hold: the raw reply and the prompt cut, a package over its ceiling omitted
    /// whole, each cut flagged. Returns the same instance when nothing is over. Applied by the writer BEFORE a trace is
    /// queued and again by <see cref="Append"/>. It never serializes and never throws, because it runs on the verdict
    /// path. The refusal reason is cut here too, on a member-for-member copy (<see cref="TurnVerdictDtoCopy"/>), so the
    /// caller's verdict is never changed and nothing is serialized.
    /// </summary>
    public static TurnVerdictTrace ApplyCeilings(TurnVerdictTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var raw = Cut(trace.RawReply, MaxRawReplyChars, out var rawCut);
        var prompt = Cut(trace.Prompt, MaxPromptChars, out var promptCut);
        // A COUNT, NOT A SERIALIZATION. This runs on the verdict path before the trace is queued, so it must stay cheap
        // and unable to throw: it adds up the text the package carries. Append checks the serialized length as well.
        var packageTooLarge = trace.Package is not null && PackageCharacters(trace.Package) > MaxPackageJsonChars;
        var reasonTooLong = trace.Verdict?.FailureReason is { Length: > MaxFailureReasonChars };
        if (!rawCut && !promptCut && !packageTooLarge && !reasonTooLong) return trace;
        return trace with
        {
            RawReply = raw,
            RawReplyTruncated = trace.RawReplyTruncated || rawCut,
            Prompt = prompt,
            PromptTruncated = trace.PromptTruncated || promptCut,
            Package = packageTooLarge ? null : trace.Package,
            PackageOmitted = trace.PackageOmitted || packageTooLarge,
            Verdict = reasonTooLong ? WithCutReason(trace.Verdict!) : trace.Verdict,
        };
    }

    /// <summary>The text a package carries, counted rather than serialized - the screen rows, the reply or failure text,
    /// the recent turns and the short facts - with a little per row for the JSON around it.</summary>
    internal static long PackageCharacters(TurnVerdictPackage package)
    {
        long total = 0;
        foreach (var row in package.ScreenRows) total += (row?.Length ?? 0) + 4;
        total += (package.LatestReply?.Length ?? 0) + (package.FailureText?.Length ?? 0) + (package.RecentTurns?.Length ?? 0)
                 + (package.SessionTitle?.Length ?? 0) + (package.FirstUserPrompt?.Length ?? 0)
                 + (package.PreviousVerdictLabel?.Length ?? 0) + (package.ScreenHash?.Length ?? 0);
        return total;
    }

    /// <summary>The verdict as this copy keeps it. Its refusal reason has already been cut by <see cref="ApplyCeilings"/>.</summary>
    private static string SerializeVerdict(TurnVerdictDto verdict) => JsonSerializer.Serialize(verdict, JsonOptions);

    /// <summary>A copy of the verdict with its refusal reason cut to the ceiling. The caller's verdict is not changed.
    /// A reason already cut is cut to the same text again.</summary>
    private static TurnVerdictDto WithCutReason(TurnVerdictDto verdict)
    {
        var copy = TurnVerdictDtoCopy.Of(verdict);
        copy.FailureReason = verdict.FailureReason![..MaxFailureReasonChars] + CutMarker;
        return copy;
    }

    /// <summary>What a cut refusal reason ends with.</summary>
    public const string CutMarker = " [cut]";

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
                Cause = row.Cause,
                VerdictId = row.VerdictId,
                ReplacedVerdictId = row.ReplacedVerdictId,
                ReplySeconds = row.ReplySeconds,
                ColourEnabled = row.ColourEnabled,
                Package = row.PackageJson is null ? null : JsonSerializer.Deserialize<TurnVerdictPackage>(row.PackageJson, JsonOptions),
                PackageOmitted = row.PackageOmitted,
                Prompt = row.Prompt,
                PromptTruncated = row.PromptTruncated,
                RawReply = row.RawReply,
                RawReplyTruncated = row.RawReplyTruncated,
                Verdict = row.VerdictJson is null ? null : JsonSerializer.Deserialize<TurnVerdictDto>(row.VerdictJson, JsonOptions),
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

    private static string? Cut(string? value, int max, out bool cut)
    {
        cut = value is not null && value.Length > max;
        return cut ? value![..max] : value;
    }

    private static void Require(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(message);
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
