using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Versions a component should NOT auto-update to, recorded when a user rolls
/// back a bad build. The update planner treats a pinned target as "up to date"
/// so silent-auto does not immediately re-stage the version that was just
/// rolled back. One pinned version per component (the last rolled-back-from).
/// </summary>
public sealed class UpdatePins
{
    private readonly Dictionary<string, string> _pins;

    public UpdatePins() => _pins = new(StringComparer.OrdinalIgnoreCase);

    private UpdatePins(Dictionary<string, string> pins) => _pins = pins;

    /// <summary>Pin <paramref name="componentId"/> away from <paramref name="version"/>.</summary>
    public void Pin(string componentId, string version)
    {
        if (string.IsNullOrWhiteSpace(componentId)) throw new ArgumentException("componentId required", nameof(componentId));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("version required", nameof(version));
        _pins[componentId] = version;
    }

    public void Clear(string componentId) => _pins.Remove(componentId);

    /// <summary>True if <paramref name="version"/> is the pinned (blocked) version for the component.</summary>
    public bool IsPinned(string componentId, string version) =>
        _pins.TryGetValue(componentId, out var pinned) &&
        VersionUtil.TryParse(pinned) is { } p &&
        VersionUtil.TryParse(version) is { } v &&
        p == v;

    public string ToJson() => JsonSerializer.Serialize(_pins);

    public static UpdatePins FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new UpdatePins();
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                  ?? new Dictionary<string, string>();
        return new UpdatePins(new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase));
    }
}
