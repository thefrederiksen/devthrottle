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

    /// <summary>The most recent brief, or null.</summary>
    public TurnBriefDto? Latest(string sessionId)
    {
        lock (_lock)
        {
            var items = LoadUnlocked(sessionId);
            return items.Count > 0 ? items[0] : null;
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
        }
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
