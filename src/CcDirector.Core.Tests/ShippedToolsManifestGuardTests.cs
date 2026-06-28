using System.Text.Json;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Guards the installed product contract: ship:true Python tools in tools/registry.json must match the
/// Core health manifest embedded in the app. This keeps the bundle builder, installer, and Tools/Home
/// health checks from silently drifting apart.
/// </summary>
public sealed class ShippedToolsManifestGuardTests
{
    [Fact]
    public void ShipTruePythonTools_MatchCoreHealthManifest()
    {
        var shipped = ShipTruePythonToolsFromRegistry();
        var manifest = ToolNamesFromCoreManifest();

        Assert.Equal(
            shipped.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            manifest.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void CcDevThrottle_IsShippedAndHealthChecked()
    {
        Assert.Contains("cc-devthrottle", ShipTruePythonToolsFromRegistry());
        Assert.Contains("cc-devthrottle", ToolNamesFromCoreManifest());
    }

    private static HashSet<string> ShipTruePythonToolsFromRegistry()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindRepoFile(Path.Combine("tools", "registry.json"))));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in doc.RootElement.GetProperty("tools").EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            var type = tool.TryGetProperty("type", out var t) ? t.GetString() : null;
            var ship = tool.TryGetProperty("ship", out var s) && s.ValueKind == JsonValueKind.True;
            if (name is not null && type == "python" && ship)
                result.Add(name);
        }
        return result;
    }

    private static HashSet<string> ToolNamesFromCoreManifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindRepoFile(Path.Combine(
            "src", "CcDirector.Core", "Tools", "tools-manifest.json"))));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in doc.RootElement.GetProperty("tools").EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is not null) result.Add(name);
        }
        return result;
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }
}
