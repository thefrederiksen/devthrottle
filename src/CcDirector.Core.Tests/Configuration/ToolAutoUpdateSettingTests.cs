using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #828: <see cref="ToolAutoUpdateSetting"/> is the read/write path the Tools settings page
/// toggle uses for <c>tools.autoUpdate.enabled</c>. The headline guarantee under test: the toggle
/// DEFAULTS ON (no config, or no key => enabled), and an explicit opt-out round-trips through the
/// settings store and survives being re-read (the persistence the toggle relies on). Each method
/// runs against an isolated CC_DIRECTOR_ROOT so the real config is never touched.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class ToolAutoUpdateSettingTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public ToolAutoUpdateSettingTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-toolautoupdate-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_NoConfig_DefaultsEnabled()
    {
        Assert.True(ToolAutoUpdateSetting.Default);
        Assert.True(ToolAutoUpdateSetting.Get()); // absent file => on
    }

    [Fact]
    public void ReadFrom_MissingSectionOrKey_DefaultsEnabled()
    {
        Assert.True(ToolAutoUpdateSetting.ReadFrom(new JsonObject()));
        Assert.True(ToolAutoUpdateSetting.ReadFrom(new JsonObject { ["tools"] = new JsonObject() }));
        Assert.True(ToolAutoUpdateSetting.ReadFrom(new JsonObject
        {
            ["tools"] = new JsonObject { ["autoUpdate"] = new JsonObject() },
        }));
    }

    [Fact]
    public void ReadFrom_NonBooleanValue_DefaultsEnabled()
    {
        // Lenient on purpose: matches the setup engine's read path so the toggle and the
        // lifecycle never disagree about the same key.
        var root = new JsonObject
        {
            ["tools"] = new JsonObject
            {
                ["autoUpdate"] = new JsonObject { ["enabled"] = "yes" },
            },
        };

        Assert.True(ToolAutoUpdateSetting.ReadFrom(root));
    }

    [Fact]
    public void ReadFrom_ExplicitFalse_IsOff()
    {
        var root = new JsonObject
        {
            ["tools"] = new JsonObject
            {
                ["autoUpdate"] = new JsonObject { ["enabled"] = false },
            },
        };

        Assert.False(ToolAutoUpdateSetting.ReadFrom(root));
    }

    [Fact]
    public void Set_False_Persists_And_SurvivesReReadOff()
    {
        ToolAutoUpdateSetting.Set(false);

        Assert.False(ToolAutoUpdateSetting.Get());                 // read back via the live store
        Assert.False(ToolAutoUpdateSetting.ReadFrom(CcDirectorConfigService.ReadRaw())); // on disk
    }

    [Fact]
    public void Set_RoundTripsBothWays()
    {
        ToolAutoUpdateSetting.Set(false);
        Assert.False(ToolAutoUpdateSetting.Get());

        ToolAutoUpdateSetting.Set(true);
        Assert.True(ToolAutoUpdateSetting.Get());
    }

    [Fact]
    public void Set_PreservesUnrelatedSections()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "https://example.test" },
        });

        ToolAutoUpdateSetting.Set(false);

        var root = CcDirectorConfigService.ReadRaw();
        Assert.False(ToolAutoUpdateSetting.ReadFrom(root));
        Assert.Equal("https://example.test", (string?)(root["gateway"] as JsonObject)?["url"]);
    }
}
