using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>The launcher presence/port fact (issue #330). Pure data; see <see cref="LauncherDiscovery"/>.</summary>
public sealed record LauncherFact(bool Installed, int? Port, string? Error);

/// <summary>
/// Reads the launcher discovery file the future cc-launcher writes on startup
/// (<c>%LOCALAPPDATA%/cc-director/config/launcher/launcher.json</c>, issue #250) and turns
/// it into the launcher fact the Gateway can pull (issue #330, plan 1B):
///
///   - File absent  -> Installed=false (a VALID fact, not an error - cc-launcher has not shipped yet).
///   - File present -> Installed=true + its declared <c>port</c>.
///   - File present but corrupt / missing the port -> Installed=true, Port=null, Error names why
///     (the file existing IS the presence fact; an unreadable port must not masquerade as "not installed").
///
/// Read at request time, never cached - the launcher may start/stop while the Director runs.
/// </summary>
public static class LauncherDiscovery
{
    /// <summary>The production discovery file location (mirrors InstanceRegistration's layout).</summary>
    public static string DefaultPath { get; } =
        Path.Combine(CcStorage.ToolConfig("launcher"), "launcher.json");

    /// <summary>Read the launcher fact. Tests pass an isolated path; production omits it.</summary>
    public static LauncherFact Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new LauncherFact(Installed: false, Port: null, Error: null);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            FileLog.Write($"[LauncherDiscovery] Read FAILED (file present but unreadable): {path}: {ex.Message}");
            return new LauncherFact(Installed: true, Port: null, Error: $"launcher.json unreadable: {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("port", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var port))
                    return new LauncherFact(Installed: true, Port: port, Error: null);
                return new LauncherFact(Installed: true, Port: null,
                    Error: $"launcher.json port is not an integer (got {property.Value.ValueKind})");
            }
            return new LauncherFact(Installed: true, Port: null, Error: "launcher.json has no port field");
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[LauncherDiscovery] Read: corrupt launcher.json at {path}: {ex.Message}");
            return new LauncherFact(Installed: true, Port: null, Error: $"launcher.json unparsable: {ex.Message}");
        }
    }
}
