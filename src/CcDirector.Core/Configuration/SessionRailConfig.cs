using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The Director rail's own remembered state: which order the two-position switch is on, and which
/// crews the user has opened. Persisted in config.json beside "sidebar_collapsed", which is the same
/// place the rail already keeps how it looks - one file, one user, one Director.
///
/// WHY THE OPEN CREWS ARE REMEMBERED AT ALL. The session list is the ownership tree and a crew is
/// COLLAPSED by default, so a crew the user opened would close itself on every restart and the user
/// would open it again every morning. The design settled it with the owner on 14 September 2026:
/// expanded or collapsed is remembered per crew, per user.
///
/// A crew is remembered by the SUPERVISOR'S SESSION ID, so the set ages out naturally - a supervisor
/// that is gone leaves an id nothing matches, which costs a few bytes and changes nothing on screen.
/// <see cref="SetExpandedCrews"/> is handed the live set on every change, so the file never grows
/// without bound while the Director runs.
///
/// THE PARSING AND THE WRITING ARE PURE AND SEPARATE (<see cref="ReadFrom"/>, <see cref="WriteInto"/>)
/// so the rules - an unknown order name falls back to my order, a malformed crew list is not fatal -
/// are testable without a config file on disk.
/// </summary>
public static class SessionRailConfig
{
    /// <summary>The config.json key for the two-position order switch.</summary>
    public const string OrderKey = "session_rail_order";

    /// <summary>The config.json key for the set of crews the user has opened.</summary>
    public const string ExpandedCrewsKey = "session_rail_expanded_crews";

    /// <summary>The order switch's "my order" position - the drag order, and the Director's default.</summary>
    public const string MyOrder = "my";

    /// <summary>The order switch's "attention" position - the Gateway's attention sections.</summary>
    public const string Attention = "attention";

    private static string _order = MyOrder;
    private static HashSet<string> _expandedCrews = new(StringComparer.Ordinal);
    private static bool _loaded;

    /// <summary>Which position the rail's order switch is on: <see cref="MyOrder"/> or <see cref="Attention"/>.</summary>
    public static string Order
    {
        get
        {
            if (!_loaded) Load();
            return _order;
        }
    }

    /// <summary>The session ids of the crews the user has opened. Empty means every crew is collapsed.</summary>
    public static IReadOnlyCollection<string> ExpandedCrews
    {
        get
        {
            if (!_loaded) Load();
            return _expandedCrews;
        }
    }

    /// <summary>Drop the cached state so the next read re-loads from config.json. Test-only.</summary>
    internal static void ResetForTests()
    {
        _loaded = false;
        _order = MyOrder;
        _expandedCrews = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Set the order switch position and persist it.</summary>
    public static void SetOrder(string order)
    {
        var normalized = Normalize(order);
        FileLog.Write($"[SessionRailConfig] SetOrder: {normalized}");
        if (!_loaded) Load();
        _order = normalized;
        Save();
    }

    /// <summary>Replace the set of opened crews and persist it.</summary>
    public static void SetExpandedCrews(IEnumerable<string> crewSessionIds)
    {
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in crewSessionIds)
        {
            var trimmed = (id ?? "").Trim();
            if (trimmed.Length > 0) next.Add(trimmed);
        }

        FileLog.Write($"[SessionRailConfig] SetExpandedCrews: {next.Count} crew(s) open");
        if (!_loaded) Load();
        _expandedCrews = next;
        Save();
    }

    /// <summary>
    /// The order name this rail will honour. An unknown or missing value is MY ORDER - the Director's
    /// default, settled in the design - never a guess and never the phone's default.
    /// </summary>
    public static string Normalize(string? order) =>
        string.Equals((order ?? "").Trim(), Attention, StringComparison.OrdinalIgnoreCase)
            ? Attention
            : MyOrder;

    /// <summary>
    /// Read the rail's state out of a parsed config.json object. Pure, so the fallbacks are tested
    /// without a file: a missing key, a wrong type, or a crew list with non-string entries all yield
    /// the default rather than throwing.
    /// </summary>
    public static (string Order, IReadOnlyCollection<string> ExpandedCrews) ReadFrom(JsonElement root)
    {
        var order = MyOrder;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(OrderKey, out var orderProp)
            && orderProp.ValueKind == JsonValueKind.String)
        {
            order = Normalize(orderProp.GetString());
        }

        var crews = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(ExpandedCrewsKey, out var crewsProp)
            && crewsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in crewsProp.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) continue;
                var id = (entry.GetString() ?? "").Trim();
                if (id.Length > 0) crews.Add(id);
            }
        }

        return (order, crews);
    }

    /// <summary>Write the rail's state into a config.json object. Pure; the caller owns the file.</summary>
    public static void WriteInto(JsonObject root, string order, IEnumerable<string> expandedCrews)
    {
        root[OrderKey] = Normalize(order);
        var array = new JsonArray();
        foreach (var id in expandedCrews.OrderBy(x => x, StringComparer.Ordinal)) array.Add(id);
        root[ExpandedCrewsKey] = array;
    }

    private static void Load()
    {
        _loaded = true;
        _order = MyOrder;
        _expandedCrews = new HashSet<string>(StringComparer.Ordinal);

        var configPath = CcStorage.ConfigJson();
        if (!File.Exists(configPath)) return;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            var (order, crews) = ReadFrom(doc.RootElement);
            _order = order;
            _expandedCrews = new HashSet<string>(crews, StringComparer.Ordinal);
            FileLog.Write($"[SessionRailConfig] Load: order={_order}, openCrews={_expandedCrews.Count}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionRailConfig] Load FAILED: {ex.Message}");
        }
    }

    private static void Save()
    {
        var configPath = CcStorage.ConfigJson();
        try
        {
            JsonNode? root;
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                root = JsonNode.Parse(json);
            }
            else
            {
                var configDir = Path.GetDirectoryName(configPath);
                if (configDir is null)
                    throw new InvalidOperationException($"Cannot determine directory for config path: {configPath}");
                Directory.CreateDirectory(configDir);
                root = new JsonObject();
            }

            if (root is JsonObject obj)
            {
                WriteInto(obj, _order, _expandedCrews);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(configPath, root.ToJsonString(options));
                FileLog.Write($"[SessionRailConfig] Save: order={_order}, openCrews={_expandedCrews.Count} to {configPath}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionRailConfig] Save FAILED: {ex.Message}");
        }
    }
}
