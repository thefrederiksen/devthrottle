using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the tooling auto-update read path (issue #827): the <c>tools.autoUpdate.enabled</c> key,
/// read through <see cref="ToolAutoUpdateConfig"/> from config.json, defaulting to TRUE when absent and
/// distinct from the Director self-update <c>autoUpdate</c> section.
/// </summary>
public class ToolAutoUpdateConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public ToolAutoUpdateConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-toolaucfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_layout.ConfigPath) ?? _layout.LocalRoot);
        File.WriteAllText(_layout.ConfigPath, json);
    }

    [Fact]
    public void Load_NoFile_ReturnsEnabledByDefault()
    {
        var c = ToolAutoUpdateConfig.Load(_layout);
        Assert.True(c.Enabled);
    }

    [Fact]
    public void Load_NoToolsSection_ReturnsEnabledByDefault()
    {
        WriteConfig("""{ "autoUpdate": { "enabled": false }, "gateway": {} }""");
        Assert.True(ToolAutoUpdateConfig.Load(_layout).Enabled);
    }

    [Fact]
    public void Load_ToolsSectionWithoutAutoUpdate_ReturnsEnabledByDefault()
    {
        WriteConfig("""{ "tools": { "somethingElse": 1 } }""");
        Assert.True(ToolAutoUpdateConfig.Load(_layout).Enabled);
    }

    [Fact]
    public void Load_AutoUpdateWithoutEnabledKey_ReturnsEnabledByDefault()
    {
        WriteConfig("""{ "tools": { "autoUpdate": { } } }""");
        Assert.True(ToolAutoUpdateConfig.Load(_layout).Enabled);
    }

    [Fact]
    public void Load_ReadsEnabledFalse()
    {
        WriteConfig("""{ "tools": { "autoUpdate": { "enabled": false } } }""");
        Assert.False(ToolAutoUpdateConfig.Load(_layout).Enabled);
    }

    [Fact]
    public void Load_ReadsEnabledTrue()
    {
        WriteConfig("""{ "tools": { "autoUpdate": { "enabled": true } } }""");
        Assert.True(ToolAutoUpdateConfig.Load(_layout).Enabled);
    }

    [Fact]
    public void Load_IsDistinctFromDirectorSelfUpdateSwitch()
    {
        // The Director self-update switch is OFF but the tooling switch is absent -> tooling defaults ON.
        // Proves tools.autoUpdate.enabled is its own key, not the top-level autoUpdate.enabled.
        WriteConfig("""{ "autoUpdate": { "enabled": false } }""");
        Assert.False(AutoUpdateConfig.Load(_layout).Enabled);
        Assert.True(ToolAutoUpdateConfig.Load(_layout).Enabled);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEnabledByDefault()
    {
        WriteConfig("{ not valid json");
        Assert.True(ToolAutoUpdateConfig.Load(_layout).Enabled);
    }
}
