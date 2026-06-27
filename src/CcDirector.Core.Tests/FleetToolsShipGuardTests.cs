using System.Text.Json;
using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Deployment guard (issue #719): the fleet command must be SHIPPED (ship:true python
/// in tools/registry.json, so the Python bundle builds them and the installer puts them on PATH)
/// AND SELF-VERIFIED (present in the embedded Core tools-manifest.json the in-app doctor reads).
/// Those two lists are hand-synced today; this guard fails if either drifts for the fleet surface, so
/// it cannot silently half-ship again - the exact gap that left #705/#717 dead on arrival in the
/// installed product.
/// </summary>
public sealed class FleetToolsShipGuardTests
{
    private static readonly string[] FleetTools = { "cc-devthrottle" };

    [Fact]
    public void FleetTools_AreShippablePythonInRegistry()
    {
        var shipped = ShipTruePythonToolsFromRegistry();
        foreach (var tool in FleetTools)
            Assert.True(shipped.Contains(tool),
                $"{tool} must be ship:true python in tools/registry.json so it builds into the bundle and deploys to PATH.");
    }

    [Fact]
    public void FleetTools_ArePresentInCoreHealthCheckManifest()
    {
        var binDir = Path.Combine(Path.GetTempPath(), "FleetToolsGuard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(binDir);
        try
        {
            var catalog = new ToolCatalogService(binDir).GetCatalog().Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var tool in FleetTools)
                Assert.True(catalog.Contains(tool),
                    $"{tool} must be in src/CcDirector.Core/Tools/tools-manifest.json so the in-app doctor verifies it.");
        }
        finally
        {
            try { Directory.Delete(binDir, recursive: true); } catch (IOException) { }
        }
    }

    private static HashSet<string> ShipTruePythonToolsFromRegistry()
    {
        var registryPath = FindRepoFile(Path.Combine("tools", "registry.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(registryPath));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in doc.RootElement.GetProperty("tools").EnumerateArray())
        {
            var type = tool.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            var ship = tool.TryGetProperty("ship", out var sh) && sh.ValueKind == JsonValueKind.True;
            var name = tool.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            if (type == "python" && ship && name is not null)
                result.Add(name);
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
