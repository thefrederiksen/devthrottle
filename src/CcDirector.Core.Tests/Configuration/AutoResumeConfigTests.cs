using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #476: <see cref="AutoResumeConfig"/> reads the opt-in transient-error auto-resume
/// settings from config.json's "auto_resume" object. The headline guarantee under test: the
/// feature DEFAULTS OFF (no config, or no "enabled" key => disabled), satisfying the "behavior
/// can be turned off => zero retries" acceptance criterion by default. Each method runs against
/// an isolated CC_DIRECTOR_ROOT.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class AutoResumeConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public AutoResumeConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-autoresume-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_NoConfig_DefaultsDisabled()
    {
        var cfg = AutoResumeConfig.Get();

        Assert.False(cfg.Enabled); // opt-in: default OFF
        Assert.Equal(AutoResumeConfig.Default, cfg);
    }

    [Fact]
    public void Get_SectionPresentButNoEnabledKey_DefaultsDisabled()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["auto_resume"] = new JsonObject { ["interval_seconds"] = 120 },
        });

        var cfg = AutoResumeConfig.Get();

        Assert.False(cfg.Enabled);                 // still OFF unless explicitly enabled
        Assert.Equal(120, cfg.IntervalSeconds);    // sibling key honored
        Assert.Equal(60, cfg.FirstRetrySeconds);   // unspecified -> default
    }

    [Fact]
    public void Get_FullSection_ReadsAllValues()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["auto_resume"] = new JsonObject
            {
                ["enabled"] = true,
                ["first_retry_seconds"] = 30,
                ["interval_seconds"] = 240,
                ["max_attempts"] = 6,
                ["max_elapsed_minutes"] = 45,
            },
        });

        var cfg = AutoResumeConfig.Get();

        Assert.True(cfg.Enabled);
        Assert.Equal(30, cfg.FirstRetrySeconds);
        Assert.Equal(240, cfg.IntervalSeconds);
        Assert.Equal(6, cfg.MaxAttempts);
        Assert.Equal(45, cfg.MaxElapsedMinutes);
        Assert.Equal(TimeSpan.FromSeconds(30), cfg.FirstRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(240), cfg.Interval);
        Assert.Equal(TimeSpan.FromMinutes(45), cfg.MaxElapsed);
    }

    [Fact]
    public void Get_EnabledWrongType_ThrowsWithFixNamed()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["auto_resume"] = new JsonObject { ["enabled"] = "yes" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => AutoResumeConfig.Get());
        Assert.Contains("auto_resume.enabled", ex.Message);
    }

    [Fact]
    public void Get_NonPositiveInterval_Throws()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["auto_resume"] = new JsonObject { ["interval_seconds"] = 0 },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => AutoResumeConfig.Get());
        Assert.Contains("auto_resume.interval_seconds", ex.Message);
    }

    [Fact]
    public void Get_SectionWrongType_Throws()
    {
        CcDirectorConfigService.MergePatch(new JsonObject { ["auto_resume"] = "on" });

        var ex = Assert.Throws<InvalidOperationException>(() => AutoResumeConfig.Get());
        Assert.Contains("auto_resume", ex.Message);
    }
}
