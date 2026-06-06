using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Briefing;

/// <summary>
/// Gateway-side durable storage for turn briefs (issue #185, locked in #173): one
/// APPEND-ONLY .jsonl file per session - one brief per line, never rewritten, never
/// ring-capped. The Director's 50-ring aged out chapter-opening cards; the Gateway store
/// is the fleet's story of record, so chapters keep their openings forever.
///
/// A regenerated brief for an already-briefed turn is APPENDED too; readers de-duplicate
/// by TurnNumber keeping the LAST occurrence (latest generation wins), which preserves
/// replace semantics for consumers without ever rewriting history on disk.
///
/// Thread-safe: a single lock serializes appends; reads return snapshots.
/// </summary>
public sealed class GatewayTurnBriefStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root;
    private readonly object _lock = new();

    // Latest-brief cache: the /sessions aggregation stamps RailLine per session on every
    // Cockpit refresh (issue #187), which must not become a disk read per session per poll.
    // Lazily filled on first read, updated on every append.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TurnBriefDto?> _latest = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TurnPackage> _packages = new();

    /// <param name="root">Storage directory; defaults to
    /// %LOCALAPPDATA%\cc-director\gateway-turnbriefs. Tests pass a temp dir.</param>
    public GatewayTurnBriefStore(string? root = null)
    {
        _root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(CcStorage.Root(), "gateway-turnbriefs")
            : root;
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string sessionId) => Path.Combine(_root, $"{Sanitize(sessionId)}.jsonl");
    private string PackageDirFor(string sessionId) => Path.Combine(_root, $"{Sanitize(sessionId)}.packages");
    private string PackagePathFor(string sessionId, int turnNumber) => Path.Combine(PackageDirFor(sessionId), $"t{turnNumber}.json");
    private string ExplainPathFor(string sessionId) => Path.Combine(_root, $"{Sanitize(sessionId)}.explain.jsonl");

    /// <summary>All stored briefs for a session, NEWEST FIRST, de-duplicated by TurnNumber
    /// (last appended generation wins). Empty when none.</summary>
    public List<TurnBriefDto> List(string sessionId)
    {
        lock (_lock)
        {
            return LoadUnlocked(sessionId);
        }
    }

    /// <summary>The most recent brief, or null. Cached in memory (poll-friendly).</summary>
    public TurnBriefDto? Latest(string sessionId)
    {
        if (_latest.TryGetValue(sessionId, out var cached)) return cached;
        lock (_lock)
        {
            var items = LoadUnlocked(sessionId);
            var latest = items.Count > 0 ? items[0] : null;
            _latest[sessionId] = latest;
            return latest;
        }
    }

    /// <summary>Append one brief (one JSON line). The file is never rewritten.</summary>
    public void Append(string sessionId, TurnBriefDto brief)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(brief);
        FileLog.Write($"[GatewayTurnBriefStore] Append: sid={sessionId}, turn={brief.TurnNumber}, model={brief.Model}, degraded={brief.Degraded}");
        lock (_lock)
        {
            File.AppendAllText(PathFor(sessionId), JsonSerializer.Serialize(brief, JsonOpts) + Environment.NewLine);
            var current = _latest.TryGetValue(sessionId, out var c) ? c : null;
            if (current is null || brief.TurnNumber >= current.TurnNumber)
                _latest[sessionId] = brief;
        }
    }

    /// <summary>Persist the exact raw-material package that produced a brief. Feedback reports
    /// embed this package so a downvote is replayable after the Gateway restarts (#207).</summary>
    public void SavePackage(string sessionId, TurnPackage package)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(package);
        FileLog.Write($"[GatewayTurnBriefStore] SavePackage: sid={sessionId}, turn={package.TurnCount}");
        lock (_lock)
        {
            Directory.CreateDirectory(PackageDirFor(sessionId));
            File.WriteAllText(PackagePathFor(sessionId, package.TurnCount), JsonSerializer.Serialize(package, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
            _packages[PackageKey(sessionId, package.TurnCount)] = package;
        }
    }

    /// <summary>Append one explain report (issue #217) - same append-only discipline as
    /// briefs: one JSON line, never rewritten; readers take the LAST line as current.</summary>
    public void AppendExplain(string sessionId, ExplainReportDto report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(report);
        FileLog.Write($"[GatewayTurnBriefStore] AppendExplain: sid={sessionId}, turn={report.TurnNumber}, model={report.Model}, degraded={report.Degraded}");
        lock (_lock)
        {
            File.AppendAllText(ExplainPathFor(sessionId), JsonSerializer.Serialize(report, JsonOpts) + Environment.NewLine);
        }
    }

    /// <summary>The newest explain report for a session, or null when none stored.</summary>
    public ExplainReportDto? LatestExplain(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_lock)
        {
            var path = ExplainPathFor(sessionId);
            if (!File.Exists(path)) return null;

            ExplainReportDto? latest = null;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { latest = JsonSerializer.Deserialize<ExplainReportDto>(line, JsonOpts) ?? latest; }
                catch (JsonException ex)
                {
                    // Torn line (power loss mid-append): logged and skipped, same as briefs.
                    FileLog.Write($"[GatewayTurnBriefStore] skipping corrupt explain line in {path}: {ex.Message}");
                }
            }
            return latest;
        }
    }

    /// <summary>Store a #207 feedback report as a replayable labeled example: vote/reason,
    /// full brief, full TurnPackage, timestamp, and brain model. Passing feedbackId updates
    /// the same one-tap record with a later typed/dictated reason.</summary>
    public TurnBriefFeedbackResponse SaveFeedback(string sessionId, TurnBriefDto brief, string vote, string? reason, string? feedbackId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(brief);
        vote = NormalizeVote(vote);
        reason = reason?.Trim() ?? "";

        var dir = Core.Storage.CcStorage.BriefFeedback();
        var now = DateTime.UtcNow;
        var id = string.IsNullOrWhiteSpace(feedbackId)
            ? $"{now:yyyyMMdd-HHmmss}-{Sanitize(sessionId)[..Math.Min(8, sessionId.Length)]}-t{brief.TurnNumber}-{vote}"
            : Sanitize(feedbackId);
        var file = Path.Combine(dir, id + ".json");

        TurnBriefFeedbackRecord record;
        if (File.Exists(file))
        {
            record = JsonSerializer.Deserialize<TurnBriefFeedbackRecord>(File.ReadAllText(file), JsonOpts) ?? new TurnBriefFeedbackRecord();
            record.Vote = vote;
            record.Reason = reason;
            record.UpdatedAtUtc = now;
        }
        else
        {
            record = new TurnBriefFeedbackRecord
            {
                FeedbackId = id,
                SessionId = sessionId,
                TurnNumber = brief.TurnNumber,
                Vote = vote,
                Reason = reason,
                BrainModel = brief.Model,
                Brief = brief,
                TurnPackage = LoadPackage(sessionId, brief.TurnNumber),
                ReportedAtUtc = now,
                UpdatedAtUtc = now,
            };
        }

        File.WriteAllText(file, JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
        FileLog.Write($"[GatewayTurnBriefStore] SaveFeedback: id={id}, vote={vote}, file={file}");
        return new TurnBriefFeedbackResponse { Saved = true, FeedbackId = id, File = file };
    }

    public List<TurnBriefFeedbackListItem> ListFeedback(int count = 50)
    {
        FileLog.Write($"[GatewayTurnBriefStore] ListFeedback: count={count}");
        var dir = Core.Storage.CcStorage.BriefFeedback();
        if (!Directory.Exists(dir)) return new List<TurnBriefFeedbackListItem>();

        return Directory.GetFiles(dir, "*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(Math.Clamp(count, 1, 500))
            .Select(ReadFeedbackListItem)
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();
    }

    private object? LoadPackage(string sessionId, int turnNumber)
    {
        var key = PackageKey(sessionId, turnNumber);
        if (_packages.TryGetValue(key, out var cached)) return cached;

        var path = PackagePathFor(sessionId, turnNumber);
        if (!File.Exists(path)) return null;

        try
        {
            var package = JsonSerializer.Deserialize<TurnPackage>(File.ReadAllText(path), JsonOpts);
            if (package is not null) _packages[key] = package;
            return package;
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[GatewayTurnBriefStore] LoadPackage FAILED: {path}: {ex.Message}");
            return null;
        }
    }

    private TurnBriefFeedbackListItem? ReadFeedbackListItem(FileInfo file)
    {
        try
        {
            var record = JsonSerializer.Deserialize<TurnBriefFeedbackRecord>(File.ReadAllText(file.FullName), JsonOpts);
            if (record is null) return null;
            return new TurnBriefFeedbackListItem
            {
                FeedbackId = string.IsNullOrWhiteSpace(record.FeedbackId) ? Path.GetFileNameWithoutExtension(file.Name) : record.FeedbackId,
                SessionId = record.SessionId,
                TurnNumber = record.TurnNumber,
                Vote = record.Vote,
                Reason = record.Reason,
                BrainModel = record.BrainModel,
                BriefHeadline = record.Brief.Headline,
                BriefRailLine = record.Brief.NeedsYou?.RailLine ?? "",
                HasTurnPackage = record.TurnPackage is not null,
                ReportedAtUtc = record.ReportedAtUtc,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLog.Write($"[GatewayTurnBriefStore] ReadFeedbackListItem FAILED: {file.FullName}: {ex.Message}");
            return null;
        }
    }

    private static string NormalizeVote(string vote)
    {
        vote = (vote ?? "").Trim().ToLowerInvariant();
        return vote switch
        {
            "up" or "thumbs_up" or "positive" => "up",
            "down" or "thumbs_down" or "negative" or "" => "down",
            _ => throw new ArgumentException($"unsupported feedback vote '{vote}'", nameof(vote)),
        };
    }

    private static string PackageKey(string sessionId, int turnNumber) => $"{sessionId}:t{turnNumber}";

    private List<TurnBriefDto> LoadUnlocked(string sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) return new List<TurnBriefDto>();

        // Later lines win per TurnNumber (regeneration replaces on READ, not on disk).
        var byTurn = new Dictionary<int, (int Order, TurnBriefDto Brief)>();
        var order = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            TurnBriefDto? brief;
            try { brief = JsonSerializer.Deserialize<TurnBriefDto>(line, JsonOpts); }
            catch (JsonException ex)
            {
                // A torn line (e.g. power loss mid-append) must not hide the rest of the
                // session's story; it is logged and skipped, never silently rewritten.
                FileLog.Write($"[GatewayTurnBriefStore] skipping corrupt line in {path}: {ex.Message}");
                continue;
            }
            if (brief is null) continue;
            byTurn[brief.TurnNumber] = (order++, brief);
        }

        return byTurn.Values
            .OrderByDescending(v => v.Brief.TurnNumber)
            .ThenByDescending(v => v.Order)
            .Select(v => v.Brief)
            .ToList();
    }

    private static string Sanitize(string sessionId)
    {
        // Session ids are GUID strings everywhere; this is defense for the file system only.
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            if (sessionId.Contains(c)) sessionId = sessionId.Replace(c, '-');
        }
        return sessionId;
    }
}
