using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Storage;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The single read/write authority for <c>config.json</c> on the C# side.
///
/// Why this exists: <c>config.json</c> holds many independent sections (<c>gateway</c>,
/// <c>llm</c>, <c>photos</c>, <c>comm_manager</c>, <c>screenshots</c>, ...) written by
/// different owners (this app, the Python cc-settings tool). Any writer that rewrites
/// the whole file from a typed model silently DROPS sections it doesn't know about.
/// To avoid that data loss, all writes here go through <see cref="MergePatch"/>, which
/// deep-merges a partial patch into the on-disk JSON and preserves every untouched key.
///
/// No-fallback rule: a malformed config.json THROWS rather than being silently reset.
/// We never overwrite a file we couldn't parse - that would destroy the user's data to
/// hide a problem.
/// </summary>
public static class CcDirectorConfigService
{
    // UI (Settings dialog) and the REST PUT /settings handler can both write at once.
    // A process-wide lock serializes the read-modify-write so neither clobbers the other.
    private static readonly object WriteLock = new();

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Read the full config.json as a mutable <see cref="JsonObject"/>. A missing file
    /// yields an empty object. A file that exists but cannot be parsed THROWS - callers
    /// must surface that, never silently reset.
    /// </summary>
    public static JsonObject ReadRaw()
    {
        var path = CcStorage.ConfigJson();
        if (!File.Exists(path))
            return new JsonObject();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        var node = JsonNode.Parse(text)
            ?? throw new InvalidOperationException($"config.json parsed to null: {path}");
        if (node is not JsonObject obj)
            throw new InvalidOperationException($"config.json root is not a JSON object: {path}");
        return obj;
    }

    /// <summary>
    /// Deep-merge <paramref name="patch"/> into config.json and write it back atomically.
    /// Object values are merged recursively; scalar and array values replace whatever was
    /// there. Keys not mentioned in the patch are left exactly as they were on disk.
    /// Returns the merged document.
    /// </summary>
    public static JsonObject MergePatch(JsonObject patch)
    {
        if (patch is null) throw new ArgumentNullException(nameof(patch));

        lock (WriteLock)
        {
            var current = ReadRaw();
            MergeInto(current, patch);
            WriteAtomic(current);
            return current;
        }
    }

    /// <summary>Gateway connection settings, read fresh from disk.</summary>
    public static GatewayConfig GetGateway() => GatewayConfig.Load();

    /// <summary>The resolved screenshots directory (config override, else platform default).</summary>
    public static string GetScreenshotsDir() => CcStorage.Screenshots();

    /// <summary>
    /// Recursively merge <paramref name="patch"/> into <paramref name="target"/>. When both
    /// sides hold an object at the same key, recurse; otherwise the patch value wins (a clone
    /// so the patch tree and the target tree never share nodes).
    /// </summary>
    private static void MergeInto(JsonObject target, JsonObject patch)
    {
        foreach (var kvp in patch)
        {
            if (kvp.Value is JsonObject patchChild
                && target[kvp.Key] is JsonObject targetChild)
            {
                MergeInto(targetChild, patchChild);
            }
            else
            {
                target[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// Write the document to config.json via a temp file + atomic move so a crash mid-write
    /// can never leave a half-written (and therefore unparseable) config.
    /// </summary>
    private static void WriteAtomic(JsonObject document)
    {
        var path = CcStorage.ConfigJson();
        var dir = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"config.json has no directory: {path}");
        Directory.CreateDirectory(dir);

        var json = document.ToJsonString(WriteOptions);
        var temp = Path.Combine(dir, $".config.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
