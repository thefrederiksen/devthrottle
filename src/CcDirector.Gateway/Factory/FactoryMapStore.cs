using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Factory;

/// <summary>One factory's published map, as kept: the map, when, and by whom.</summary>
public sealed record StoredFactoryMap(PublishFactoryMapRequest Map, DateTime PublishedUtc, string PublishedBy);

/// <summary>
/// The factory maps (issue #3383): the latest map each factory published, one JSON object per account held in the
/// account's settings under <see cref="TenantSettingKeys.FactoryMaps"/>, keyed by factory id. A publish replaces
/// that factory's map and leaves the others alone.
///
/// A map is refused whole when any part of it is wrong - an unknown kind, an arrow to a box that is not there, a
/// coordinate that is not a finite number inside the drawing, more boxes or words than a map needs - so what the
/// Cockpit draws is always a map a factory really published, never a half of one.
/// </summary>
public sealed partial class FactoryMapStore
{
    public const int MaxFactories = 50;
    public const int MaxNodes = 80;
    public const int MaxEdges = 250;
    public const int MaxSpecRows = 20;
    public const int MaxLines = 4;
    public const int MaxPoints = 400;
    public const int MaxShortChars = 120;
    public const int MaxTextChars = 600;
    public const double MaxExtent = 20_000;

    /// <summary>Route words under /factory-agents that a factory id may not be, or its page could not be reached.</summary>
    private static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "waiting" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    private readonly object _gate = new();
    private readonly TenantSettingsStore _settings;

    public FactoryMapStore(TenantSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>The factory's map, or null when it has published none. A stored value that cannot be read is an
    /// error, never "no map".</summary>
    public StoredFactoryMap? Find(TenantId tenant, string factory)
    {
        var all = All(tenant);
        return all.TryGetValue(factory, out var map) ? map : null;
    }

    /// <summary>Keep a factory's map, replacing its last one. Throws <see cref="FactoryViewValidationException"/>
    /// with the reason when refused; nothing is stored then.</summary>
    public StoredFactoryMap Publish(TenantId tenant, PublishFactoryMapRequest? map, string publishedBy, DateTime nowUtc)
    {
        FileLog.Write($"[FactoryMapStore] Publish: factory={map?.Factory}, nodes={map?.Nodes?.Count}, edges={map?.Edges?.Count}, by={publishedBy}");
        Validate(map);
        var stored = new StoredFactoryMap(map!, nowUtc, publishedBy);
        lock (_gate)
        {
            var all = All(tenant);
            if (!all.ContainsKey(map!.Factory) && all.Count >= MaxFactories)
                throw new FactoryViewValidationException($"An account keeps at most {MaxFactories} factory maps.");
            all[map.Factory] = stored;
            _settings.Set(tenant, TenantSettingKeys.FactoryMaps, JsonSerializer.Serialize(all, Json), nowUtc);
        }
        FileLog.Write($"[FactoryMapStore] Publish: stored {map.Factory}");
        return stored;
    }

    private Dictionary<string, StoredFactoryMap> All(TenantId tenant)
    {
        var raw = _settings.Get(tenant, TenantSettingKeys.FactoryMaps);
        if (string.IsNullOrWhiteSpace(raw))
            return new Dictionary<string, StoredFactoryMap>(StringComparer.OrdinalIgnoreCase);
        var read = JsonSerializer.Deserialize<Dictionary<string, StoredFactoryMap>>(raw, Json)
                   ?? throw new InvalidOperationException("The factory maps setting holds no object.");
        return new Dictionary<string, StoredFactoryMap>(read, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every rule a published map must meet. Throws with the first one it breaks.</summary>
    internal static void Validate(PublishFactoryMapRequest? map)
    {
        if (map is null) throw Refuse("A map body is required.");
        Id(map.Factory, "factory");
        if (Reserved.Contains(map.Factory)) throw Refuse($"A factory cannot be called '{map.Factory}'.");
        Text(map.Title, "title", MaxShortChars, required: true);
        Text(map.Source, "source", MaxTextChars, required: true);
        Extent(map.Width, "width", positive: true);
        Extent(map.Height, "height", positive: true);

        if (map.Nodes is null || map.Nodes.Count == 0) throw Refuse("A map needs at least one box.");
        if (map.Nodes.Count > MaxNodes) throw Refuse($"A map holds at most {MaxNodes} boxes.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in map.Nodes)
        {
            if (n is null) throw Refuse("A box is empty.");
            Id(n.Id, "box id");
            if (!ids.Add(n.Id)) throw Refuse($"Two boxes are called '{n.Id}'.");
            if (!FactoryMapNodeKind.All.Contains(n.Kind ?? ""))
                throw Refuse($"Box '{n.Id}' has kind '{n.Kind}'; a box is one of {string.Join(", ", FactoryMapNodeKind.All)}.");
            Text(n.Title, $"box '{n.Id}' title", MaxShortChars, required: true);
            if (n.Lines is null || n.Lines.Count > MaxLines) throw Refuse($"Box '{n.Id}' has more than {MaxLines} lines.");
            foreach (var line in n.Lines) Text(line, $"a line of box '{n.Id}'", MaxShortChars, required: true);
            Extent(n.X, $"box '{n.Id}' x");
            Extent(n.Y, $"box '{n.Id}' y");
            Extent(n.Width, $"box '{n.Id}' width", positive: true);
            Extent(n.Height, $"box '{n.Id}' height", positive: true);
            if (n.Spec is null || n.Spec.Count > MaxSpecRows) throw Refuse($"Box '{n.Id}' has more than {MaxSpecRows} spec rows.");
            foreach (var r in n.Spec)
            {
                if (r is null) throw Refuse($"A spec row of box '{n.Id}' is empty.");
                Text(r.Label, $"a spec label of box '{n.Id}'", MaxShortChars, required: true);
                Text(r.Text, $"the spec row '{r.Label}' of box '{n.Id}'", MaxTextChars, required: true);
            }
        }

        if (map.Edges is null) throw Refuse("A map needs a list of arrows, even an empty one.");
        if (map.Edges.Count > MaxEdges) throw Refuse($"A map holds at most {MaxEdges} arrows.");
        foreach (var e in map.Edges)
        {
            if (e is null) throw Refuse("An arrow is empty.");
            var name = $"the arrow {e.From} to {e.To}";
            if (!ids.Contains(e.From ?? "") || !ids.Contains(e.To ?? ""))
                throw Refuse($"{name} names a box that is not on the map.");
            if (!FactoryMapEdgeKind.All.Contains(e.Kind ?? ""))
                throw Refuse($"{name} has kind '{e.Kind}'; an arrow is one of {string.Join(", ", FactoryMapEdgeKind.All)}.");
            if (e.Result is not null && !FactoryMapResults.All.Contains(e.Result))
                throw Refuse($"{name} listens for '{e.Result}'; a run finishes only as {string.Join(", ", FactoryMapResults.All)}.");
            Text(e.Label, $"the words on {name}", MaxShortChars, required: false);
            if (e.Points is null || e.Points.Count < 4 || (e.Points.Count - 1) % 3 != 0 || e.Points.Count > MaxPoints)
                throw Refuse($"{name} needs a start point then whole curves (1 + 3n points, at most {MaxPoints}).");
            foreach (var p in e.Points) Point(p, name);
            if (e.Tip is not null) Point(e.Tip, name);
            if (e.LabelX is not null || e.LabelY is not null)
            {
                if (e.LabelX is null || e.LabelY is null) throw Refuse($"{name} has half a label position.");
                Extent(e.LabelX.Value, $"{name} label x");
                Extent(e.LabelY.Value, $"{name} label y");
            }
        }
    }

    private static void Id(string? id, string what)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !IdPattern().IsMatch(id))
            throw Refuse($"The {what} '{id}' must be lower-case letters and digits joined by hyphens, at most 64 characters.");
    }

    private static void Text(string? text, string what, int max, bool required)
    {
        if (text is null || (required && text.Trim().Length == 0)) throw Refuse($"The {what} is missing.");
        if (text.Length > max) throw Refuse($"The {what} is longer than {max} characters.");
    }

    private static void Extent(double v, string what, bool positive = false)
    {
        if (!double.IsFinite(v) || v < 0 || v > MaxExtent || (positive && v <= 0))
            throw Refuse($"The {what} ({v}) must be a number from {(positive ? "above " : "")}0 to {MaxExtent}.");
    }

    private static void Point(double[]? p, string what)
    {
        if (p is null || p.Length != 2) throw Refuse($"A point of {what} is not an x and a y.");
        Extent(p[0], $"{what} point x");
        Extent(p[1], $"{what} point y");
    }

    private static FactoryViewValidationException Refuse(string why) => new(why);
}

/// <summary>The four ways a factory agent's run finishes: the only thing a chain listens for.</summary>
public static class FactoryMapResults
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string NothingToDo = "nothing-to-do";
    public const string NeedsYou = "needs-you";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Succeeded, Failed, NothingToDo, NeedsYou };
}
