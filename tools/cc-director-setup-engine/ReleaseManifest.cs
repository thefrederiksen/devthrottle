using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>One asset entry inside a release manifest.</summary>
/// <param name="Name">Asset filename (the key, e.g. "cc-pdf-win-x64.exe").</param>
/// <param name="Version">
/// The component's OWN version. This is what enables independent-per-component
/// updates: a release can bump one asset's version without touching the others.
/// </param>
/// <param name="Sha256">Expected SHA-256 (hex) of the asset.</param>
/// <param name="Platform">"windows" / "macos" / "unknown".</param>
/// <param name="Size">Asset size in bytes (0 if unknown).</param>
public sealed record ManifestAsset(string Name, string Version, string Sha256, string Platform, long Size);

/// <summary>
/// A parsed release-manifest.json. Carries a top-level release version plus a
/// per-asset map. Each asset MAY declare its own version; when it does not, it
/// inherits the release version (this keeps pre-per-asset manifests readable -
/// a defined inheritance rule, not an error-hiding fallback).
/// </summary>
public sealed class ReleaseManifest
{
    /// <summary>The release tag/version (e.g. "0.4.0").</summary>
    public required string Version { get; init; }

    /// <summary>Assets keyed by filename.</summary>
    public required IReadOnlyDictionary<string, ManifestAsset> Assets { get; init; }

    public ManifestAsset? TryGetAsset(string assetName) =>
        Assets.TryGetValue(assetName, out var a) ? a : null;

    /// <summary>
    /// Parse manifest JSON. Throws <see cref="FormatException"/> on a structurally
    /// invalid manifest (missing version or assets) - we do not silently accept a
    /// manifest we cannot trust.
    /// </summary>
    public static ReleaseManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("Release manifest is empty.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.String)
            throw new FormatException("Release manifest has no top-level string 'version'.");
        var releaseVersion = versionEl.GetString()!;

        if (!root.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("Release manifest has no 'assets' object.");

        var assets = new Dictionary<string, ManifestAsset>(StringComparer.Ordinal);
        foreach (var prop in assetsEl.EnumerateObject())
        {
            var entry = prop.Value;
            var sha = GetStringOrEmpty(entry, "sha256");
            var platform = GetStringOrEmpty(entry, "platform");
            var size = entry.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
                ? sizeEl.GetInt64()
                : 0L;
            var assetVersion = entry.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String
                ? vEl.GetString()!
                : releaseVersion; // inherit the release version when an asset omits its own

            assets[prop.Name] = new ManifestAsset(prop.Name, assetVersion, sha, platform, size);
        }

        return new ReleaseManifest { Version = releaseVersion, Assets = assets };
    }

    private static string GetStringOrEmpty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()! : "";
}
