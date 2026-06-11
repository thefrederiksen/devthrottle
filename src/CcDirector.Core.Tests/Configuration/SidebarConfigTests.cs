using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="SidebarConfig"/>, the persisted collapsed/expanded state of the
/// desktop session sidebar. The contract: missing file/key = expanded (false), SetCollapsed
/// round-trips through config.json, and saving never clobbers unrelated config.json keys.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class SidebarConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public SidebarConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-sidebar-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        SidebarConfig.ResetForTests();
    }

    public void Dispose()
    {
        SidebarConfig.ResetForTests();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
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
    public void Collapsed_MissingConfig_ReturnsFalse()
    {
        Assert.False(SidebarConfig.Collapsed);
    }

    [Fact]
    public void Collapsed_KeyAbsent_ReturnsFalse()
    {
        SeedConfig("""{ "default_session_mode": "Terminal" }""");
        Assert.False(SidebarConfig.Collapsed);
    }

    [Fact]
    public void SetCollapsed_True_RoundTripsThroughDisk()
    {
        SidebarConfig.SetCollapsed(true);

        // Force a re-load from config.json so we assert the persisted value, not the cache.
        SidebarConfig.ResetForTests();
        Assert.True(SidebarConfig.Collapsed);
    }

    [Fact]
    public void SetCollapsed_FalseAfterTrue_RoundTripsThroughDisk()
    {
        SidebarConfig.SetCollapsed(true);
        SidebarConfig.SetCollapsed(false);

        SidebarConfig.ResetForTests();
        Assert.False(SidebarConfig.Collapsed);
    }

    [Fact]
    public void SetCollapsed_PreservesUnrelatedConfigKeys()
    {
        SeedConfig("""{ "default_session_mode": "Studio", "brain_model": "claude-opus-4-8" }""");

        SidebarConfig.SetCollapsed(true);

        var json = File.ReadAllText(CcStorage.ConfigJson());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Studio", doc.RootElement.GetProperty("default_session_mode").GetString());
        Assert.Equal("claude-opus-4-8", doc.RootElement.GetProperty("brain_model").GetString());
        Assert.True(doc.RootElement.GetProperty("sidebar_collapsed").GetBoolean());
    }

    [Fact]
    public void Collapsed_NonBoolValue_ReturnsFalse()
    {
        SeedConfig("""{ "sidebar_collapsed": "yes" }""");
        Assert.False(SidebarConfig.Collapsed);
    }
}
