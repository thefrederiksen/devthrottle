using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
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

    /// <summary>
    /// Store a D7 "this brief is wrong" report as a labeled example for prompt iteration:
    /// the brief and the user's note, one JSON file per report under brief-feedback/
    /// (issue #187: the feedback loop moved to the Gateway with the rest of the pipeline).
    /// </summary>
    public string SaveFeedback(string sessionId, TurnBriefDto brief, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var dir = Core.Storage.CcStorage.BriefFeedback();
        var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Sanitize(sessionId)[..Math.Min(8, sessionId.Length)]}-t{brief.TurnNumber}.json");
        var payload = new { sessionId, note, brief, reportedAtUtc = DateTime.UtcNow };
        File.WriteAllText(file, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
        FileLog.Write($"[GatewayTurnBriefStore] SaveFeedback: {file}");
        return file;
    }

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
