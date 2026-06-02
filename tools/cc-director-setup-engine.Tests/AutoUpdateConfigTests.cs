using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class AutoUpdateConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public AutoUpdateConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-aucfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"), Path.Combine(_dir, "pf"), Path.Combine(_dir, "pd"));
        Environment.SetEnvironmentVariable("CC_AUTOUPDATE", null); // ensure no kill switch leaks in
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_AUTOUPDATE", null);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_layout.ConfigPath) ?? _layout.LocalRoot);
        File.WriteAllText(_layout.ConfigPath, json);
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var c = AutoUpdateConfig.Load(_layout);
        Assert.True(c.Enabled);
        Assert.Equal(6, c.IntervalHours);
    }

    [Fact]
    public void Load_NoAutoUpdateSection_ReturnsDefaults()
    {
        WriteConfig("""{ "llm": {}, "gateway": {} }""");
        var c = AutoUpdateConfig.Load(_layout);
        Assert.True(c.Enabled);
        Assert.Equal(6, c.IntervalHours);
    }

    [Fact]
    public void Load_ReadsEnabledAndInterval()
    {
        WriteConfig("""{ "autoUpdate": { "enabled": false, "intervalHours": 2 } }""");
        var c = AutoUpdateConfig.Load(_layout);
        Assert.False(c.Enabled);
        Assert.Equal(2, c.IntervalHours);
        Assert.Equal(TimeSpan.FromHours(2), c.Interval);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        WriteConfig("{ not valid json");
        Assert.True(AutoUpdateConfig.Load(_layout).Enabled);
    }

    [Fact]
    public void Load_EnvKillSwitch_OverridesConfig()
    {
        WriteConfig("""{ "autoUpdate": { "enabled": true, "intervalHours": 1 } }""");
        Environment.SetEnvironmentVariable("CC_AUTOUPDATE", "0");
        Assert.False(AutoUpdateConfig.Load(_layout).Enabled);
    }

    [Fact]
    public void Interval_ClampsTinyValuesToSixHours()
    {
        WriteConfig("""{ "autoUpdate": { "intervalHours": 0 } }""");
        Assert.Equal(TimeSpan.FromHours(6), AutoUpdateConfig.Load(_layout).Interval);
    }
}
