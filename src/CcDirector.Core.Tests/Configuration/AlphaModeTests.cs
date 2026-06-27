using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="AlphaMode"/>, the global alpha-features toggle. The contract:
/// default OFF (missing/absent key = disabled), persisted as "alpha_mode" through the
/// round-trip-preserving config writer (sibling sections must survive), and Changed
/// fires on toggle so live windows can re-gate. AlphaMode caches statically, so every
/// test calls Reload() after pointing CC_DIRECTOR_ROOT at its isolated temp root.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class AlphaModeTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public AlphaModeTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-alpha-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        AlphaMode.Reload();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        AlphaMode.Reload();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void SeedConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void IsEnabled_MissingConfig_DefaultsToFalse()
    {
        Assert.False(AlphaMode.IsEnabled);
    }

    [Fact]
    public void IsEnabled_KeyAbsent_DefaultsToFalse()
    {
        SeedConfig("""{ "gateway": { "url": "http://gw.example:7878" } }""");
        AlphaMode.Reload();

        Assert.False(AlphaMode.IsEnabled);
    }

    [Fact]
    public void IsEnabled_KeyTrue_ReturnsTrue()
    {
        SeedConfig("""{ "alpha_mode": true }""");
        AlphaMode.Reload();

        Assert.True(AlphaMode.IsEnabled);
    }

    [Fact]
    public void SetEnabled_PersistsAndPreservesSiblingSections()
    {
        SeedConfig("""
        {
          "gateway": { "url": "http://gw.example:7878", "token": "keep-me" },
          "screenshots": { "source_directory": "C:/shots" }
        }
        """);
        AlphaMode.Reload();

        AlphaMode.SetEnabled(true);

        Assert.True(AlphaMode.IsEnabled);
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.True((bool?)onDisk["alpha_mode"]);
        // Untouched sibling sections must survive the write.
        var gateway = onDisk["gateway"];
        Assert.NotNull(gateway);
        Assert.Equal("http://gw.example:7878", (string?)gateway["url"]);
        Assert.Equal("keep-me", (string?)gateway["token"]);
        var screenshots = onDisk["screenshots"];
        Assert.NotNull(screenshots);
        Assert.Equal("C:/shots", (string?)screenshots["source_directory"]);

        AlphaMode.SetEnabled(false);
        Assert.False(AlphaMode.IsEnabled);
        Assert.False((bool?)CcDirectorConfigService.ReadRaw()["alpha_mode"]);
    }

    [Fact]
    public void SetEnabled_RaisesChanged()
    {
        var fired = 0;
        Action handler = () => fired++;
        AlphaMode.Changed += handler;
        try
        {
            AlphaMode.SetEnabled(true);
            Assert.Equal(1, fired);
        }
        finally
        {
            AlphaMode.Changed -= handler;
        }
    }

    [Fact]
    public void Reload_PicksUpExternalWrite()
    {
        AlphaMode.SetEnabled(false);

        // Simulate an external writer (cc-devthrottle settings, REST PUT) flipping the flag.
        CcDirectorConfigService.MergePatch(new JsonObject { ["alpha_mode"] = true });
        AlphaMode.Reload();

        Assert.True(AlphaMode.IsEnabled);
    }
}
