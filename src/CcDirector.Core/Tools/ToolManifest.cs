using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Tools;

/// <summary>
/// The deserialized shape of <c>tools-manifest.json</c> (embedded in this assembly). This is the
/// authoritative declaration of which tools exist and what their read-only smoke command is. The
/// universal presence + version checks are not declared here - the catalog adds them to every tool.
/// </summary>
public sealed class ToolManifest
{
    [JsonPropertyName("tools")]
    public List<ToolManifestEntry> Tools { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Load the embedded manifest. Throws if the resource is missing or malformed - we never
    /// silently return an empty catalog, which would hide a packaging break.
    /// </summary>
    public static ToolManifest LoadEmbedded()
    {
        var json = ReadEmbeddedResource("tools-manifest.json");
        var manifest = JsonSerializer.Deserialize<ToolManifest>(json, Options)
            ?? throw new InvalidOperationException("tools-manifest.json deserialized to null");
        return manifest;
    }

    /// <summary>
    /// Read an embedded JSON resource from this assembly by its file-name suffix. Throws when the
    /// resource cannot be found so a missing embed fails loudly at first use.
    /// </summary>
    internal static string ReadEmbeddedResource(string fileNameSuffix)
    {
        var asm = typeof(ToolManifest).Assembly;
        var resourceName = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith(fileNameSuffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded resource not found: {fileNameSuffix}. Available: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource stream null: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>One tool entry in the manifest.</summary>
public sealed class ToolManifestEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "General";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("smoke")]
    public ToolManifestSmoke? Smoke { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>A declared read-only smoke command.</summary>
public sealed class ToolManifestSmoke
{
    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("expectContains")]
    public string? ExpectContains { get; set; }
}
