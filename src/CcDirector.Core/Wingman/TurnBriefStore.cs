using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Durable per-session storage for wingman turn briefs (TURN_BRIEFING.md D4/D5): one JSON
/// file per session under <see cref="CcStorage.TurnBriefs"/>, holding a ring of the most
/// recent briefs. Survives Director restarts - the briefs ARE the session's story of record,
/// shared by every consumer (Brief page, rail, FIFO, voice).
///
/// Thread-safe: a single lock serializes writes; reads return snapshots.
/// </summary>
public sealed class TurnBriefStore
{
    /// <summary>Briefs kept per session. Old ones age out; the JSONL transcript remains the
    /// deep history.</summary>
    public const int RingSize = 50;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly object _lock = new();

    /// <param name="root">Storage directory; defaults to <see cref="CcStorage.TurnBriefs"/>.
    /// Tests pass a temp dir.</param>
    public TurnBriefStore(string? root = null)
    {
        _root = string.IsNullOrWhiteSpace(root) ? CcStorage.TurnBriefs() : root;
        Directory.CreateDirectory(_root);
    }

    private string PathFor(Guid sessionId) => Path.Combine(_root, $"{sessionId}.json");

    /// <summary>All stored briefs for a session, NEWEST FIRST. Empty when none.</summary>
    public List<TurnBriefDto> List(Guid sessionId)
    {
        lock (_lock)
        {
            return LoadUnlocked(sessionId);
        }
    }

    /// <summary>The most recent brief, or null.</summary>
    public TurnBriefDto? Latest(Guid sessionId)
    {
        lock (_lock)
        {
            var items = LoadUnlocked(sessionId);
            return items.Count > 0 ? items[0] : null;
        }
    }

    /// <summary>
    /// Append a brief (newest first, ring-capped). A brief for an already-briefed TurnNumber
    /// REPLACES the prior one (regeneration / degrade-tier upgrade), never duplicates.
    /// </summary>
    public void Append(Guid sessionId, TurnBriefDto brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        FileLog.Write($"[TurnBriefStore] Append: sid={sessionId}, turn={brief.TurnNumber}, model={brief.Model}, degraded={brief.Degraded}");
        lock (_lock)
        {
            var items = LoadUnlocked(sessionId);
            items.RemoveAll(b => b.TurnNumber == brief.TurnNumber);
            items.Insert(0, brief);
            if (items.Count > RingSize)
                items.RemoveRange(RingSize, items.Count - RingSize);
            File.WriteAllText(PathFor(sessionId), JsonSerializer.Serialize(items, JsonOpts));
        }
    }

    /// <summary>
    /// Store a D7 "this brief is wrong" report as a labeled example: the brief, the turn
    /// package text, and the user's note, one JSON file per report under brief-feedback/.
    /// </summary>
    public string SaveFeedback(Guid sessionId, TurnBriefDto brief, string? packageText, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var dir = CcStorage.BriefFeedback();
        var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sessionId.ToString()[..8]}-t{brief.TurnNumber}.json");
        var payload = new { sessionId, note, brief, packageText, reportedAtUtc = DateTime.UtcNow };
        File.WriteAllText(file, JsonSerializer.Serialize(payload, JsonOpts));
        FileLog.Write($"[TurnBriefStore] SaveFeedback: {file}");
        return file;
    }

    private List<TurnBriefDto> LoadUnlocked(Guid sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) return new List<TurnBriefDto>();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<TurnBriefDto>>(json, JsonOpts) ?? new List<TurnBriefDto>();
    }
}
